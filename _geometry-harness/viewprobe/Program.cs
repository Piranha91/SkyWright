using System.Diagnostics;
using System.Numerics;
using HousecarlCore;
using HousecarlCore.Progress;

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
// TEXTURE ROUTE PIN. CharacterViewer.Rendering's default DDS route is authored-mip compressed
// uploads, which change captured pixels (mostly normal-mapped surfaces and alpha edges) while
// geometry, depth and masks stay exact. Retained captures, frozen hashes and receipts were all made
// on the decoded route, and the configuration fingerprint does not yet record the texture policy,
// so viewprobe stays decoded until it does. The library reads this once per AssetCache
// construction, so it must be set before the first scene is built. An explicitly set value wins,
// which is how a run opts in to compressed uploads deliberately.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CVR_DDS_MAX_FINAL_MIP_DIMENSION")))
    Environment.SetEnvironmentVariable("CVR_DDS_MAX_FINAL_MIP_DIMENSION", "decoded");
// LAUNCHED FROM EXPLORER — a double-click, or a worklist DROPPED ON THE ICON. Registered FIRST,
// ahead of the Require() calls just below, because an unconfigured harness exits from inside those:
// a window that closes on the one message explaining how to configure it has taught nobody
// anything. The console dies with the process on a GUI launch, so every exit path pauses.
bool __batchFailureBoundary = args.Contains("--worklist-selection", StringComparer.Ordinal)
    || args.Contains("--progress-dir", StringComparer.Ordinal)
    || args.Contains("--worklist-readiness-sha256", StringComparer.Ordinal);
// Widened once the arguments are parsed: reuse is the default on a static worklist, so the boundary
// covers those runs too (see the reuseCellExplicit check after the argument loop).
bool __reuseFailureBoundary = args.Contains("--reuse-cell", StringComparer.Ordinal);
try
{
using var __run = RunLog.BeginProcess("viewprobe", args,
    // A render is not one indivisible step and no longer says it is: RenderStages.cs opens one
    // stage per phase, which is what makes the coverage manifest's `counted` claim true.
    new RunOptions { StageSource = StageSource.Counted });
// THE EXTRACTION SCRATCH. The rendering library unpacks BSA assets to disk before it can read
// them, and every process started from one build folder used to unpack them to the SAME place —
// so two renders started together overwrote each other's files and produced quietly wrong
// pictures at exit 0. Each process now gets its own folder; this is what removes it again on
// every path out of Main, including the early returns just below (where nothing was claimed and
// it does nothing at all). See ExtractionScratch.cs.
using var __scratch = ViewProbe.ExtractionScratch.ProcessScope();

if (args.Length == 2 && args[0] == "--report-mode-smoke")
    return ViewProbe.TexturedScene.ReportModeSmoke(args[1]);
if (args.Length == 1 && args[0] == "--nav-selection-smoke")
    return ViewProbe.NavmeshSelection.Smoke();
if (args.Length == 1 && args[0] == "--nav-link-smoke")
    return ViewProbe.NavmeshLinks.Smoke();
// The deleter, on the command line, so its contract can be tested against a directory state a test
// owns rather than against whatever a render happened to leave. Ahead of the harness Require()
// calls below: it needs no MO2 instance, no game and no load order.
if (args.Length >= 2 && args[0] == "--extraction-scratch-sweep")
    return ViewProbe.ExtractionScratch.Sweep(args);
// The mesh half of the unreadable-asset decision, over files given on the command line, so a test
// can put a REAL mesh in front of the real decision and read the answer. Needs no MO2 instance.
if (args.Length >= 2 && args[0] == "--unreadable-asset-smoke")
    return ViewProbe.UnreadableAssets.MeshSmoke(args[1..]);
// The three ways a scratch failure travels - the claim throwing, the guard's refusal escaping, the
// same refusal swallowed by a catch-all - each reachable from the command line, so a test can prove
// they all end at one code with one line and no stack trace. Needs no MO2 instance.
if (args.Length >= 2 && args[0] == "--extraction-scratch-fail")
    return ViewProbe.ExtractionScratchExit.FailSelfTest(args[1]);
// The path budget's two decisions - is this path too long for the library's reader, and does this
// root leave an asset any room - over paths on the command line. Both are decisions about a string,
// so a test can stand one character either side of the limit. Needs no MO2 instance.
if (args.Length >= 2 && args[0] == "--asset-path-budget")
    return ViewProbe.AssetPathBudget.Report(args[1..]);
// The visibility-mask evidence writer - how a mask is derived from the isolated depth buffer, how
// it is identified, and that a repeat written from the cache is byte-identical to one encoded
// again - over buffers built in the test rather than rendered, so a check can stand one depth
// value either side of a mask pixel. Needs no MO2 instance.
if (args.Length >= 2 && args[0] == "--mask-evidence-selftest")
    return ViewProbe.MaskEvidenceSelfTest.Run(args[1]);
// The report sink's two promises - the same bytes the string route wrote, and never a half-written
// report under the real name. The second needs a process to be killed in the middle of a write, so
// the slow arm is here too. Needs no MO2 instance.
if (args.Length >= 2 && args[0] == "--report-sink-selftest")
    return ViewProbe.ReportSinkSelfTest.Run(args[1]);
if (args.Length >= 3 && args[0] == "--report-sink-kill-arm")
    return ViewProbe.ReportSinkSelfTest.KillArm(args[1], int.Parse(args[2]));
// The --reuse-cell lease's three promises - one set of shared objects per cell, a new set for the
// next cell, and nothing at all once the cell is done - over objects built in the test, so a check
// can hold the lifetime itself. Needs no MO2 instance, no game and no GL context.
// Known answers for the occlusion rule's orientation scope (cases 6000-6014): the rule loader and Shadow on planes
// built in the test. Needs no MO2 instance, no game and no GL context.
if (args.Length >= 2 && args[0] == "--occlusion-rule-selftest")
    return ViewProbe.OcclusionRuleSelfTest.Run(args[1]);
if (args.Length >= 1 && args[0] == "--planes-report-selftest")
    return ViewProbe.PlanesReportCandidatesSelfTest.Run();
if (args.Length >= 1 && args[0] == "--cell-scene-selftest")
    return ViewProbe.CellSceneSelfTest.Run();
// Known answers for the depth-measurement mode's texture keep rule and mask rule (DepthMeasurement.cs). Needs no MO2
// instance, no game and no GL context.
if (args.Length >= 1 && args[0] == "--depth-measurement-selftest")
    return ViewProbe.DepthMeasurement.SelfTest();
// Known answers for --occlusion-query on this machine's GPU (OcclusionQuerySelfTest.cs): the sampled-depth conversion
// against ReadPixels over every depth value a full-range fill reaches, and the query's counts against the CPU compare on
// synthetic depth with ties, uncovered pixels and hidden pixels. Needs a GL context, no MO2 instance and no game.
if (args.Length >= 1 && args[0] == "--occlusion-query-selftest")
    return ViewProbe.OcclusionQuerySelfTest.Run();
// Known answers for which placed references the winning state admits, and where (WinningRefs.cs), on
// Mutagen-written synthetic plugins. Needs no MO2 instance, no game and no GL context.
if (args.Length >= 1 && args[0] == "--winning-refs-selftest")
    return ViewProbe.WinningRefsSelfTest.Run();
// What the render does when a plugin, or one record in it, cannot be read: a Mutagen-written master and override,
// damaged byte by byte, run through this executable as a child process. Needs no MO2 instance, no game and no GL context.
if (args.Length >= 1 && args[0] == "--plugin-read-selftest")
    return ViewProbe.PluginReadSelfTest.Run();
// Known answers for the side tables the walk fills (the --stations floor, the "where is it" tables) and the route mode's
// worldspace pass, against moved and deleted references (_shared/MovedRefFixture.cs). No MO2 instance, game or GL context.
// The capture-fallback fixture (CaptureFixture.cs): one plugin of interior cells naming vanilla meshes, for the capture tests.
if (args.Length == 2 && args[0] == "--write-capture-fixture")
    return ViewProbe.CaptureFixture.Write(args[1]);
if (args.Length >= 1 && args[0] == "--moved-ref-selftest")
    return ViewProbe.MovedRefSelfTestVp.Run();
if (args.Length == 2 && args[0] == "--mesh-reuse-red")
{
    try { return ViewProbe.MeshReuseRed.Run(args[1]); }
    catch (Exception ex)
    {
        Console.Error.WriteLine("mesh reuse selftest unexpected exception:\n" + ex);
        return 2;
    }
}

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
ViewProbe.GrassLayer.ProfileDir = profileDir;   // grass reads the profile's [Grass] INI settings (GrassLayer.cs)
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

if (args.Length > 0 && args[0] == "--smoke") return Smoke();
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: viewprobe <CellEditorID|FormID> [--out DIR] [--size N] [--angles 0,90,180] [--elev 25] [--no-nav] [--ref FORMKEY[=COLOR]]... [--ref-surface [--ref-surface-opacity 0<v<=1]] [--report-mode] [--interactive [--fly-speed 600] [--look-sens 0.15]]");
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
float canvasAspect = 1f;                         // --aspect: capture canvas width / height; 1 = the historic square
float elev = 28f;
bool drawNav = true;
var highlightSpecs = new List<string>();
var angles = new List<float> { 35f, 125f };
// First-person textured mode ("see what the tester will see"): real textures via
// CharacterViewer.Rendering, camera INSIDE the cell at eye height, game-style FOV.
// --eye/--eye-ref implies --tex. Coordinates are SKYRIM's (a `getpos` line pastes straight in);
// yaw matches the game's Z angle (0 = north/+Y, clockwise), positive pitch looks down.
bool textured = false;
bool reportMode = false;                         // clean scene pixels for report/video capture; metadata stays external
bool refSurface = false;                         // exact, depth-tested opaque subject contribution; no halo or ghost
// --ref-surface-opacity: the subject overlay's own alpha. 1 is the shipped opaque mark and
// stays the default, so an omitted flag renders the same pixels it always did. Below 1 the
// mark is composited over the subject's OWN material, which is the only thing that changes:
// same placed triangles, same depth test, same refusals, no inflation and no ghost.
float refSurfaceOpacity = 1f;
bool refSurfaceOpacitySet = false;               // given explicitly -> "without --ref-surface" is an error, not a silent no-op
System.Numerics.Vector3? eye = null, look = null;
string? eyeRef = null, lookRef = null;
float eyeHeight = 120f;                       // standing eye level; the player is 128u tall
float? yaw = null; float pitch = 0f;
float fov = 65f;                              // viewprobe's own default (the VERTICAL angle of its square frame); the game's
                                              // `fDefaultWorldFOV` read 80 on SkyrimSE 1.7.104 (2026-09-05) - shotplan.py maps one to the other
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
// fxStatic (the default since 2026-10-29; --no-fx turns it off): effect-shader shapes are DRAWN
// through SkyRender's engine effect shading, frozen at the frame the NIF stores — flames, smoke,
// light shafts and mists, including whole-file `effects\` refs. It covers only the effect-shader
// kind: a normal map in the diffuse slot stays skipped, since no blend mode makes that shadeable.
// --no-fx restores the old default (both kinds and `effects\` files dropped), for a frame where an
// additive flame must not paint over the navmesh. --fx-static is kept as an explicit spelling of
// the default.
bool fxStatic = true;
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
// OCCLUSION PLANES (see OcclusionPlanes.cs). --planes draws every plane in the cell (+ ring) as a
// translucent quad with an outline, coloured by who put it there; --occlusion off|on|diff previews
// the culling: `on` leaves out the refs the planes would cull from this eye, `diff` draws everything
// and tints the culled refs red so the change is visible in one frame; --planes-report lists each
// plane with what it culls and the nearest solid collision behind it within --planes-reach u. All
// off by default, and the default render is byte-identical to what it was before they existed.
bool showPlanes = false;
// --bos off | on | diff — the Base Object Swapper layer (housecarl-core/BaseObjectSwaps.cs): on (the default) draws
// every ref with the base and transform the game loads after *_SWAP.ini; diff the same, swapped refs tinted teal
// and named in the hover; off the record bases, as every render did before 2026-09-04.
string bosMode = "on";
// --reuse-cell / --no-reuse-cell — cell reuse (CellScene.cs), ON by default for a static worklist
// since 2026-09-24. Without it a worklist's shots are built one per SHOT, so two shots of one cell do
// the camera-independent work twice; with it the shots of a cell share a lease that is constructed
// for the cell and dropped when the cell is done. --no-reuse-cell restores the per-shot path, every
// shot building its own exactly as before; --reuse-cell is still accepted and changes nothing.
bool reuseCell = true;
bool reuseCellExplicit = false;
bool noReuseCell = false;
// --state SPEC — the world state (roadmap 4.11, _shared/WorldState.cs): <switch>[,<switch>…] by editor id or FormKey,
// stage:<quest>:<n> with --switch-map (switchprobe's --json), or baseline. Every ref's existence becomes its runtime
// enable state with those switches flipped. --state-view on (default) draws the state; diff also draws what the flip
// took out, arrivals tinted GREEN and departures RED (StateView.cs). Without --state the picture is the load-time state,
// as every render before 2026-09-04.
string? stateSpec = null, switchMapPath = null;
string? orderFile = null;
// --config-identity: report WHAT THIS RUN CONSUMED when no candidate file replaced it. The
// identity is the same one --order emits (HarnessOrderCli.Resolve with no candidate resolves the
// profile itself), so nothing new is hashed or named here. It is opt-in because resolving it
// hashes every loaded plugin, and a caller that probes a cell six times should pay that once.
bool configIdentity = false;
string stateView = "on";
string occlusionMode = "off";
bool planesReport = false;
// --depth-measurement: a --planes-report run without the appearance work (DepthMeasurement.cs); --keep-all-masks
// keeps every evidence mask under it.
bool depthMeasurement = false, keepAllMasks = false;
// --occlusion-query: with --depth-measurement, count each shape's pixels on the GPU (OcclusionQuery.cs).
bool occlusionQuery = false;
int visibilityBudget = ViewProbe.OcclusionState.DefaultVisibilityBudget;
float planesReach = 256f;
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
var navCellSpecs = new List<string>();
var navVersionSpecs = new List<string>();
// --nav-link SOURCE=TARGET: the source's edges that cross into the target, measured from the records (NavmeshLinks.cs).
var navLinkSpecs = new List<string>();
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
// --seams [EPS]: draw every ring-cell edge whose shared LAND vertices disagree by more than EPS u (default 8, one
// heightmap unit) as a ribbon coloured by the step; --water-arm truth|winner: which arm's cell-water tuple the
// surfaces are drawn from (roadmap 4.9, 2026-09-04). Both are the render-side of waterprobe / seamprobe.
bool drawSeams = false;
float seamEps = 8f;
string waterArm = "winner";
bool drawCoverings = true;
// Exterior REFS load for the target cell and its ±1 ring, matching the terrain (defect fix,
// 2026-08-28: neighbours contributed terrain and nothing else, so any eye-level shot showed
// near objects, then void, then bare ring terrain — the College arcade 700 u away simply never
// loaded). Navmesh and the conflict overlay stay TARGET-CELL-ONLY: they are the annotation
// layer for the cell under examination, same scoping as the cell outline. --no-ring-refs goes
// back to target-cell-only refs (the old, cheaper behaviour). Interiors have no ring.
bool ringRefs = true;
// --ring visible|N (VisibleCells.cs): the loaded exterior cells. Unset (or 1) is the target and its ±1 ring, as above,
// through exactly the old code. N > 1 loads the square of radius N; visible loads every cell whose ground the frame can
// see (rays through the frame against each cell's LAND height band), out to --ring-reach cells from the eye's cell and at
// most --ring-cap cells, the target's 3x3 always included. Terrain, water and full references are loaded in all of them,
// beyond the game's ±2 grid too (the game draws LOD there); the console says how many cells and why any were cut.
string ringMode = "1";
bool liveScene = false;   // --live: a host adds and drops cells of the --ring square live (LiveScene.cs)
int ringReach = ViewProbe.VisibleCells.DefaultReach, ringCap = ViewProbe.VisibleCells.DefaultCap;
HashSet<(int X, int Y)>? visibleSet = null;   // per shot: null = the ±1 ring
// --large-refs-child union|none: which large-reference list a CHILD worldspace (a city) uses (WinningRefs.cs). union,
// the default, is what lane L read in game: the city's own listed references loaded beyond ring 2.
bool largeChildUnion = true;
// --winning-refs-dump FILE [--cells LIST] [--dump-terrain]: write the walk's winning state per target cell as one JSON line
// and draw nothing (WinningRefsDump.cs).
string? winRefsDump = null, dumpCellsFile = null;
bool dumpTerrain = false;
// In --compare, mark the navmesh vertices one plugin's version has and the other's does not with
// small spheres, in every panel that contains them. --no-nav-diff drops them.
bool navDiff = true;
// --refs-at X,Y[,R] lists every winning reference near a spot and says, per ref, whether the
// render draws it and why not. "Is there an object here that isn't being rendered?" is a
// question the picture cannot answer, because the answer is a thing you cannot see.
var refsAt = new List<(float X, float Y, float R)>();
// --inside-at X,Y,Z names every physical winning reference whose OBND box, placed by the ref's
// own transform (SpatialOverlap.ToWorld — the engine's composition), ENCLOSES a world point.
// "Is this camera standing inside a bookcase" is a question the navmesh test cannot answer
// (navmesh runs up to furniture, and the placement test allows a camera off-mesh) and the depth
// probe answers WRONG: a knee-height probe looks out through the open back of the case and
// reports clear — two low cameras in a row sat inside one (2026-09-03). Every enclosing box is
// printed with its dimensions; what a room-scale box means is the caller's decision, so no cap
// is applied here. A shape is solid by its own data, whatever folder its mesh is in: effect-shader shapes (clouds, glow
// cards, flames, editor markers), shapes hidden by APP_CULLED and lit refraction shapes (heat haze) are not solid to
// --inside-at or --clear-at. Their triangles are left out of the rays; a ref drawn only by effect shapes is listed
// "effect-only, not solid", one with no solid shape for another reason "no solid shape, not solid". A shape with no
// shader stays solid. Limit: both queries test DRAWN geometry, so a non-solid shape's own collision is ignored too.
var insideAt = new List<(float X, float Y, float Z)>();
// --clear-at X,Y,Z asks the question --inside-at structurally cannot: what placed mesh geometry
// is actually AROUND this world point, in every direction, whether or not any box encloses it.
// --inside-at only ray-tests refs whose OBND box already contains the point, so a point with no
// enclosing box gets ZERO rays and an empty answer — and an empty answer reads exactly like a
// clear one. A camera standing in the void behind a corridor wall and a camera standing in the
// middle of the corridor both return "no physical ref's box encloses it" (three vanilla Ratway
// cameras, 2026-09-14). The distinction is what is near the point, which needs real geometry: the
// broad-phase-then-transformed-soup pattern --planes-report uses. Six world-axis rays plus a
// long floor ray, against every candidate's own placed mesh, both faces. The broad phase is
// OBND-based, so a ref with no usable box is INVISIBLE to it — the count is printed, and an
// empty answer is stated as OBND-limited coverage rather than as proven clearance.
var clearAt = new List<(float X, float Y, float Z)>();
float clearReach = 512f;   // --clear-reach: how far each ray looks before reporting nothing
// --clear-bounds nif|obnd: which box selects a broad-phase candidate. `nif` (the default) uses the
// mesh's own bounds; `obnd` restores the record-box filter this probe shipped with, so the two can
// be compared on real data instead of the difference being asserted.
string clearBounds = "nif";
// --inside-bounds nif|obnd: which box NOMINATES a ref for the --inside-at ray test. Same choice,
// same reason, as --clear-bounds; `obnd` restores the record-box nomination for comparison.
string insideBounds = "nif";
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
// --nav-z Z[,TOL] | auto[,TOL]: draw the navmesh of ONE STOREY. This is the other half of the
// knife above, and it exists because --cull-above only ever held one: a five-storey interior
// sliced at the ground floor shows THAT storey's architecture under a navmesh sheet that is the
// UNION of all five, and the question a top-down is taken to answer — "where can I walk at z
// 1424" — cannot be read off it at all. Filtering the overlay to one height turns the same shot
// into a walkable floor plan.
//
// A triangle is ON the storey when its own z RANGE meets the band [Z-TOL, Z+TOL]. Range rather
// than centroid because the bottom steps of a staircase belong to the floor they rise from, and
// a centroid test drops any step taller than the tolerance; a triangle that genuinely spans two
// storeys is drawn on both, which is what it is. TOL defaults to 64 u — half the ~128 u that
// separates a floor from the one above it in most kits, so two storeys can never both qualify.
//
// `auto` takes Z from --route-z, or the shot's measured floorZ, in that order: on a route shot
// the storey of interest is the route's own, always. The filter applies to the filled sheet, the
// wire and the dashed hidden layers alike, and the caption names it — a navmesh missing from a
// picture must SAY it was filtered, or the absence reads as a finding.
float? navZ = null; float navZTol = 64f; bool navZAuto = false;
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
//   --route-solve              treat the waypoints as ENDPOINTS and PATHFIND between them over the
//                              navmesh's own connectivity graph, instead of demanding that the
//                              straight line between each pair be walkable. Opt-in, because the
//                              straight default is the one that catches a path drawn through a wall
//                              — but authoring a route under it means solving the navigation problem
//                              by hand, one probe at a time, which is what this removes. Two things
//                              come out of it that a straight leg cannot give: a path that follows
//                              the ground an actor would actually take, and its LENGTH against the
//                              straight-line distance — "1,800 u apart" and "1,800 u to walk" are
//                              different claims. An unreachable pair is REFUSED, loudly: that is a
//                              finding about the mesh, not an error to paper over with a straight
//                              line. See housecarl-core/NavGraph.cs for where the edges come from.
//   --route-z Z                the floor height the route runs on, used to pick the right STOREY
//                              when several cover a waypoint. Absent, it is the shot's measured
//                              floorZ, or eye minus --eye-height — and that last one is an
//                              assumption a top-down camera makes badly, since its eye is a
//                              thousand units above the ground the route is on.
// --grid N|auto (textured mode): rule a LABELLED world-coordinate grid across the storey the shot
// is about, with the coordinate written where each line meets the frame edge. It exists to delete
// a step, not to decorate: without it, turning something seen in a top-down into an X,Y means
// arithmetic done by eye against a screenshot and the caption's pose, and that arithmetic is where
// silent errors come from — a route planned off a mis-scaled pixel measurement looks exactly like
// one planned off a correct one. It also makes a plan far easier to HAND to someone, which matters
// when the output is "here is the path to walk".
//   auto        picks a 1-2-5 round spacing putting roughly 9 lines across the frame
//   --grid-color NAME|R,G,B   the grid's colour; RESERVED, like the route's — the nav wire's
//                             auto-pick is told to avoid it, or a grid ruled across the whole
//                             frame can come out the same hue as the mesh under it.
// Only meaningful on a steep top-down: at eye level the lines converge on the horizon and the
// renderer DECLINES with a note rather than drawing something that looks like a scale and is not.
// The plane is the storey's — --nav-z, then --route-z, then the shot's floorZ, then the aim point.
float? gridSpacing = null; bool gridOn = false;
string gridColorName = "grey";
var gridColor = new System.Numerics.Vector4(0.82f, 0.80f, 0.74f, 1f);
var routeXY = new List<(float X, float Y)>();
string routeColorName = "cyan";
var routeColor = new System.Numerics.Vector4(0.05f, 0.85f, 0.95f, 1f);
float? routeZ = null;
float? routeArrowEvery = null;                // null = distance-scaled; 0 = one per segment, at the end
string routeArrowsSpec = "auto";
bool routeDiscs = true;
bool routeSolve = false;
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
// Renderer-driver plumbing. These are one opt-in unit: the selection retains original worklist
// coordinates, the progress directory receives create-new shot receipts, and the readiness value
// independently pins the identity written inside the selection document.
string? worklistSelectionPath = null;
string? progressDir = null;
string? worklistReadinessSha256 = null;
int worklistSelectionCount = 0, progressDirCount = 0, worklistReadinessCount = 0;
// --bats-profiles <A,B>: enable that mod in these profiles and DISABLE it in every other profile of
// the instance. Opt-in, because it edits MO2's own modlist.txt.
var batsProfiles = new List<string>();
// --depth-dump FILE: write the finished frame's depth buffer — the array --pick unprojects — to a
// file, verbatim (size*size float32 window depth, GL rows bottom-up, 1.0 = background). Opt-in,
// one explicit-camera capture only; a caller that wants to read a whole frame's depth instead of
// asking pixel by pixel (the depth-survey demonstration, 2026-09-14).
string? depthDump = null;
// --stations SPEC.json / --stations-out POSES.json: compose the camera stations for this cell's
// occlusion primitives and write the pose file. It renders NOTHING — it rides the load-order walk
// the renderer already does, composes, writes, and exits. The logic is StationMode.cs; the rule it
// calls is HarnessShared.StationRule, with proofprobe's cases on it.
string? stationsSpec = null;
string? stationsOut = null;
for (int i = argStart; i < args.Length; i++)
{
    if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
    else if (args[i] == "--size" && i + 1 < args.Length) size = int.Parse(args[++i]);
    // --aspect W:H (or a decimal) makes the CAPTURE canvas the shape the camera was planned for.
    // --size stays the WIDTH and the height follows, so the default 1:1 is the historic square and
    // every existing command renders the identical frame. --fov is the VERTICAL angle either way
    // (OpenTK's CreatePerspectiveFieldOfView takes fovy), so widening the aspect widens the
    // HORIZONTAL extent and leaves the vertical alone -- which is exactly the frame shotplan plans
    // against with its own --aspect. Refused, never ignored, where it cannot take effect.
    else if (args[i] == "--aspect" && i + 1 < args.Length)
    {
        var spec = args[++i];
        var p = spec.Split(':');
        bool ok = p.Length == 2
            ? float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var aw)
              & float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ah)
              && ah > 0 && aw > 0 && (canvasAspect = aw / ah) > 0
            : float.TryParse(spec, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out canvasAspect) && canvasAspect > 0;
        if (!ok)
        { Console.Error.WriteLine($"--aspect wants W:H (e.g. 16:9) or a positive decimal; got '{spec}'."); return 2; }
    }
    else if (args[i] == "--elev" && i + 1 < args.Length) elev = float.Parse(args[++i]);
    else if (args[i] == "--no-nav") drawNav = false;
    else if (args[i] == "--ref" && i + 1 < args.Length) highlightSpecs.Add(args[++i]);
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
    else if (args[i] == "--no-fx") fxStatic = false;
    else if (args[i] == "--nav" && i + 1 < args.Length) { navMode = args[++i].ToLowerInvariant(); navModeExplicit = true; }
    else if (args[i] == "--conflicts" && i + 1 < args.Length) conflictsPath = args[++i];
    else if (args[i] == "--red" && i + 1 < args.Length) redSpecs.Add(args[++i]);
    else if (args[i] == "--nav-plugin" && i + 1 < args.Length) navPlugin = args[++i];
    else if (args[i] == "--nav-link")
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        { Console.Error.WriteLine("--nav-link requires SOURCE_NAVM=TARGET_NAVM, both full FormKeys."); return 2; }
        navLinkSpecs.Add(args[++i]);
    }
    else if (args[i] is "--nav-cell" or "--nav-version")
    {
        var option = args[i];
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        { Console.Error.WriteLine($"{option} requires a full FormKey{(option == "--nav-version" ? "=Plugin.esp" : "[=colour]")}."); return 2; }
        (option == "--nav-cell" ? navCellSpecs : navVersionSpecs).Add(args[++i]);
        textured = true; // these annotation options use the textured renderer's colour/depth paths
    }
    else if (args[i] == "--compare" && i + 1 < args.Length)
    { var p = args[++i].Split(','); if (p.Length == 2) compare = (p[0].Trim(), p[1].Trim()); }
    else if (args[i] == "--no-terrain") drawTerrain = false;
    else if (args[i] == "--no-water") drawWater = false;
    else if (args[i] == "--no-coverings") drawCoverings = false;
    else if (args[i] == "--no-terrain-layers") drawTerrainLayers = false;
    else if (args[i] == "--weather" && i + 1 < args.Length) ViewProbe.SceneWeather.Requested = args[++i];
    else if (args[i] == "--weather-slot" && i + 1 < args.Length) ViewProbe.SceneWeather.RequestedSlot = args[++i].ToLowerInvariant();
    else if (args[i] == "--no-imagespace") ViewProbe.SceneWeather.ImageSpaceOff = true;
    else if (args[i] == "--no-weather") { ViewProbe.SceneWeather.Auto = false; ViewProbe.SceneWeather.Requested = null; }
    else if (args[i] == "--cell-lighting") ViewProbe.CellLighting.Requested = true;
    else if (args[i] == "--no-cell-lighting") ViewProbe.CellLighting.Requested = false;
    else if (args[i] == "--shadows") ViewProbe.SceneWeather.ShadowsRequested = true;
    else if (args[i] == "--no-shadows") ViewProbe.SceneWeather.ShadowsRequested = false;
    else if (args[i] == "--shadow-distance" && i + 1 < args.Length) ViewProbe.SceneWeather.ShadowDistance = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--adapt-lum" && i + 1 < args.Length) ViewProbe.SceneWeather.AdaptedLuminance = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--game-hour" && i + 1 < args.Length) ViewProbe.SceneWeather.GameHour = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--sky" && i + 1 < args.Length)
    {
        ViewProbe.SceneWeather.SkyGiven = true;
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
    else if (args[i] == "--depth-dump" && i + 1 < args.Length) { depthDump = args[++i]; textured = true; }
    else if (args[i] == "--list-terrain") listTerrain = true;
    else if (args[i] == "--find-ltex" && i + 1 < args.Length) findLtex = args[++i];
    else if (args[i] == "--no-cell-outline") cellOutline = false;
    else if (args[i] == "--no-ring-refs") ringRefs = false;
    else if (args[i] == "--lod") ViewProbe.DistantLodLayer.Requested = true;
    else if (args[i] == "--live") liveScene = true;
    else if (args[i] == "--no-grass") ViewProbe.GrassLayer.Requested = false;
    else if (args[i] == "--grass") ViewProbe.GrassLayer.Requested = true;
    else if (args[i] == "--ring" && i + 1 < args.Length)
    {
        ringMode = args[++i].ToLowerInvariant();
        if (ringMode == "auto") ringMode = "visible";
        if (ringMode != "visible" && !(int.TryParse(ringMode, out var rn) && rn is >= 1 and <= 16))
        { Console.Error.WriteLine($"--ring takes visible (or auto) or a radius 1..16, not '{ringMode}'."); return 2; }
        if (ringMode != "visible") ringMode = int.Parse(ringMode, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    else if (args[i] is "--ring-reach" or "--ring-cap")
    {
        var opt = args[i];
        int lim = opt == "--ring-reach" ? 32 : 2000;
        if (i + 1 >= args.Length || !int.TryParse(args[++i], out var rv) || rv < 1 || rv > lim)
        { Console.Error.WriteLine($"{opt} takes a whole number in 1..{lim}."); return 2; }
        if (opt == "--ring-reach") ringReach = rv; else ringCap = rv;
    }
    else if (args[i] == "--large-refs-child" && i + 1 < args.Length)
    {
        var v = args[++i];
        if (v is not ("union" or "none")) { Console.Error.WriteLine($"--large-refs-child takes union or none, not '{v}'."); return 2; }
        largeChildUnion = v == "union";
    }
    else if (args[i] == "--winning-refs-dump" && i + 1 < args.Length) winRefsDump = args[++i];
    else if (args[i] == "--cells" && i + 1 < args.Length) dumpCellsFile = args[++i];
    else if (args[i] == "--dump-terrain") dumpTerrain = true;
    else if (args[i] == "--no-nav-diff") navDiff = false;
    else if (args[i] is "--nav-at" or "--refs-at" or "--inside-at" or "--clear-at" && i + 1 < args.Length)
    {
        // Refused, not dropped: a point query that loses its point prints nothing, which reads like no answer at all.
        var opt = args[i]; var spec = args[++i];
        var (want, shape) = opt switch { "--nav-at" => (2, "X,Y"), "--refs-at" => (3, "X,Y[,R]"), _ => (3, "X,Y,Z") };
        var p = spec.Split(',');
        var v = new float[p.Length];
        bool ok = (opt == "--refs-at" ? p.Length is 2 or 3 : p.Length == want);
        for (int k = 0; ok && k < p.Length; k++) ok = float.TryParse(p[k], out v[k]);
        if (!ok) { Console.Error.WriteLine($"{opt} '{spec}' is not {shape}: give {(opt == "--refs-at" ? "two or three" : want == 2 ? "two" : "three")} numbers separated by commas, with no spaces."); return 2; }
        if (opt == "--nav-at") navAt.Add((v[0], v[1]));
        else if (opt == "--refs-at") refsAt.Add((v[0], v[1], v.Length > 2 ? v[2] : 256f));
        else if (opt == "--inside-at") insideAt.Add((v[0], v[1], v[2]));
        else clearAt.Add((v[0], v[1], v[2]));
    }
    else if (args[i] == "--clear-reach" && i + 1 < args.Length) clearReach = float.Parse(args[++i]);
    else if (args[i] == "--clear-bounds" && i + 1 < args.Length) clearBounds = args[++i].ToLowerInvariant();
    else if (args[i] == "--inside-bounds" && i + 1 < args.Length) insideBounds = args[++i].ToLowerInvariant();
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
    else if (args[i] == "--route-solve") { routeSolve = true; textured = true; }
    else if (args[i] == "--grid" && i + 1 < args.Length)
    {
        var spec = args[++i];
        gridOn = true;
        if (spec.Equals("auto", StringComparison.OrdinalIgnoreCase)) gridSpacing = null;
        else if (float.TryParse(spec, out var gs) && gs > 0f) gridSpacing = gs;
        else { Console.Error.WriteLine($"--grid '{spec}' is not 'auto' or a positive number of world units."); return 1; }
        textured = true;
    }
    else if (args[i] == "--grid-color" && i + 1 < args.Length)
    {
        var spec = args[++i];
        var p = spec.Split(',');
        if (p.Length == 3)
        {
            var v = new System.Numerics.Vector3(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2]));
            if (v.X > 1f || v.Y > 1f || v.Z > 1f) v /= 255f;      // 0-255 or 0-1, like --sky
            gridColor = new System.Numerics.Vector4(v, 1f);
            gridColorName = $"{v.X:0.##},{v.Y:0.##},{v.Z:0.##}";
        }
        else { gridColorName = spec.ToLowerInvariant(); gridColor = default; }   // resolved against the table below
    }
    else if (args[i] == "--cull-above" && i + 1 < args.Length) cullAbove = float.Parse(args[++i]);
    else if (args[i] == "--nav-z" && i + 1 < args.Length)
    {
        // Refused, not shrugged off, for the --route reason: a filter that mis-parses its band
        // draws a DIFFERENT storey and looks exactly as convincing as the right one.
        var spec = args[++i];
        var p = spec.Split(',');
        if (p.Length is < 1 or > 2)
        { Console.Error.WriteLine($"--nav-z '{spec}' is not Z[,TOL] or auto[,TOL]."); return 1; }
        if (p[0].Equals("auto", StringComparison.OrdinalIgnoreCase)) navZAuto = true;
        else if (float.TryParse(p[0], out var nz)) navZ = nz;
        else { Console.Error.WriteLine($"--nav-z '{spec}': '{p[0]}' is neither a z nor 'auto'."); return 1; }
        if (p.Length == 2)
        {
            if (!float.TryParse(p[1], out var ntol) || ntol <= 0f)
            { Console.Error.WriteLine($"--nav-z '{spec}': tolerance '{p[1]}' must be a positive number of units."); return 1; }
            navZTol = ntol;
        }
        textured = true;
    }
    else if (args[i] == "--interactive") { interactiveMode = true; textured = true; }
    // --map-tiles FILE.json: draw the built scene from straight above over each listed rectangle (MapTiles.cs)
    else if (args[i] == "--map-tiles" && i + 1 < args.Length)
    {
        if (ViewProbe.MapTiles.CannotDraw is { } why) { Console.Error.WriteLine(why); return 2; }
        try { ViewProbe.MapTiles.Requested = ViewProbe.MapTiles.Read(args[++i]); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { Console.Error.WriteLine(ex is InvalidDataException ? ex.Message : $"the map tile file could not be read: {ex.Message}"); return 2; }
        textured = true;
    }
    else if (args[i] == "--report-mode") { reportMode = true; textured = true; }
    else if (args[i] == "--ref-surface") { refSurface = true; textured = true; }
    // the capture fallbacks (CaptureViews.cs): hide, protect, contacts, occluders, coverage
    else if (args[i] == "--coverage-of" && i + 1 < args.Length) { ViewProbe.CaptureViews.CoverageOf.Add(args[++i]); textured = true; }
    else if (args[i] == "--coverage-dump") ViewProbe.CaptureViews.CoverageDump = true;
    else if (args[i] == "--hide" && i + 1 < args.Length) { ViewProbe.CaptureViews.Hide.Add(args[++i]); textured = true; }
    else if (args[i] == "--protect" && i + 1 < args.Length) { ViewProbe.CaptureViews.Protect.Add(args[++i]); textured = true; }
    else if (args[i] == "--contacts-of" && i + 1 < args.Length) { ViewProbe.CaptureViews.ContactsOf.Add(args[++i]); textured = true; }
    else if (args[i] == "--occluders-of" && i + 1 < args.Length) { ViewProbe.CaptureViews.OccludersOf.Add(args[++i]); textured = true; }
    else if (args[i] == "--ref-surface-opacity" && i + 1 < args.Length)
    {
        // (0,1]. 0 is refused rather than clamped: a mark that draws nothing is a silent
        // failure wearing a valid-looking argument. A missing value falls through to the
        // unrecognised-flag branch below, which already says so and exits 2.
        if (!float.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var rso)
            || !(rso > 0f) || rso > 1f)
        {
            Console.Error.WriteLine($"--ref-surface-opacity '{args[i]}': expected a number in (0,1]. "
                                  + "1 is the opaque mark; 0 would draw nothing at all.");
            return 1;
        }
        refSurfaceOpacity = rso; refSurfaceOpacitySet = true;
    }
    else if (args[i] == "--fly-speed" && i + 1 < args.Length) flySpeed = float.Parse(args[++i]);
    else if (args[i] == "--look-sens" && i + 1 < args.Length) lookSens = float.Parse(args[++i]);
    else if (args[i] == "--worklist" && i + 1 < args.Length) worklistPaths.Add(args[++i]);
    else if (args[i] == "--worklist-selection" && i + 1 < args.Length) { worklistSelectionCount++; worklistSelectionPath = args[++i]; }
    else if (args[i] == "--progress-dir" && i + 1 < args.Length) { progressDirCount++; progressDir = args[++i]; }
    else if (args[i] == "--worklist-readiness-sha256" && i + 1 < args.Length) { worklistReadinessCount++; worklistReadinessSha256 = args[++i]; }
    else if (args[i] == "--emit-bats" && i + 1 < args.Length) emitBats = args[++i];
    else if (args[i] == "--bats-profiles" && i + 1 < args.Length)
        batsProfiles.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    else if (args[i] == "--markers") showMarkers = true;
    else if (args[i] == "--list-meshes") listMeshes = true;
    else if (args[i] == "--seams") { drawSeams = true; textured = true; if (i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var se)) { seamEps = se; i++; } }
    else if (args[i] == "--water-arm" && i + 1 < args.Length) { waterArm = args[++i].ToLowerInvariant(); textured = true; }
    else if (args[i] == "--planes") { showPlanes = true; textured = true; }
    else if (args[i] == "--occlusion" && i + 1 < args.Length) { occlusionMode = args[++i].ToLowerInvariant(); textured = true; }
    else if (args[i] == "--planes-report") { planesReport = true; textured = true; }
    else if (args[i] == ViewProbe.DepthMeasurement.Flag) depthMeasurement = true;
    else if (args[i] == ViewProbe.DepthMeasurement.KeepAllMasksFlag) keepAllMasks = true;
    else if (args[i] == ViewProbe.OcclusionQueryPass.Flag) occlusionQuery = true;
    // The deliberate opt-out from failing on an asset that was found and could not be read. It is
    // also honoured as an environment variable so an existing caller can pass it through without
    // its command line changing - and whenever either of them is what suppressed the exit code, the
    // census block, the report block and the run record all say so and name which one.
    else if (args[i] == ViewProbe.UnreadableAssets.OptOutFlag) ViewProbe.UnreadableAssets.OptOutByFlag();
    else if (args[i] == "--visibility-budget")
    {
        if (i + 1 >= args.Length || !int.TryParse(args[++i], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out visibilityBudget)
            || visibilityBudget < 1 || visibilityBudget > ViewProbe.OcclusionState.MaximumVisibilityBudget)
        {
            Console.Error.WriteLine($"--visibility-budget requires an integer in 1..{ViewProbe.OcclusionState.MaximumVisibilityBudget} (default {ViewProbe.OcclusionState.DefaultVisibilityBudget}).");
            return 1;
        }
    }
    else if (args[i] == "--occlusion-rule" && i + 1 < args.Length) ViewProbe.ShapeCullPolicy.Load(args[++i]);
    // textured, because the load walk this mode rides — the winning refs, the navmesh, the cells'
    // water — is inside that block. Nothing is drawn: the mode returns before the render section.
    else if (args[i] == "--stations" && i + 1 < args.Length) { stationsSpec = args[++i]; textured = true; }
    else if (args[i] == "--stations-out" && i + 1 < args.Length) stationsOut = args[++i];
    else if (args[i] == "--planes-reach" && i + 1 < args.Length) planesReach = float.Parse(args[++i]);
    else if (args[i] == "--reuse-cell") reuseCellExplicit = true;
    else if (args[i] == "--no-reuse-cell") { noReuseCell = true; reuseCell = false; }
    else if (args[i] == "--bos" && i + 1 < args.Length)
    {
        bosMode = args[++i].ToLowerInvariant();
        if (bosMode is not ("off" or "on" or "diff")) { Console.Error.WriteLine("--bos takes off | on | diff"); return 1; }
        if (bosMode == "diff") textured = true;
    }
    else if (args[i] == "--order")
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        { Console.Error.WriteLine("--order requires a candidate configuration file."); return 2; }
        orderFile = args[++i];
    }
    else if (args[i] == "--config-identity") configIdentity = true;
    else if (args[i] == "--state" && i + 1 < args.Length) { stateSpec = args[++i]; textured = true; }
    // --flags is refused rather than added (Q27): the renderer has drawn the load-time enable chain since 2026-08-28,
    // and a flag-only scene would draw a room the game never shows.
    else if (args[i] == "--flags")
    {
        try { HarnessShared.PopulationCli.Resolve("viewprobe", true, null, flagsSupported: false); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); }
        return 2;
    }
    else if (args[i] == "--state-view" && i + 1 < args.Length)
    {
        stateView = args[++i].ToLowerInvariant();
        if (stateView is not ("on" or "diff")) { Console.Error.WriteLine("--state-view takes on | diff"); return 1; }
        textured = true;
    }
    else if (args[i] == "--switch-map" && i + 1 < args.Length) switchMapPath = args[++i];
    else if (args[i] is "--instance" or "--game" or "--profile") i++;   // consumed above
    // A worklist path is a worklist WHEREVER it lands, not only in the leading run that a drop
    // produces: `viewprobe --interactive tour.json` is the obvious thing to type once dropping one
    // works. This loop has no unknown-argument arm, so without this the file would go on the floor
    // and the run would die with "no cell given" while the user was looking straight at the cell
    // they gave. Flag VALUES never reach here — every branch above consumes its own with args[++i].
    else if (IsWorklistFile(args[i])) worklistPaths.Add(args[i]);
    // Refuse unknown flags: silently dropping a camera argument can select an overview instead
    // of the requested capture. Flag values are consumed above, so an unmatched `--` token is
    // unknown or a known flag whose value is missing. The parser does not accept --flag=value.
    else if (args[i].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"viewprobe does not recognise '{args[i]}'. Either this build has "
                              + "no such flag, or it is a flag whose VALUE is missing.");
        Console.Error.WriteLine("  NOTE: there is no --flag=value form here. Write `--eye X,Y,Z`, "
                              + "not `--eye=X,Y,Z` — the second is not a flag this reads.");
        return 2;
    }
}
if (ringMode != "1" && !ringRefs)
{
    Console.Error.WriteLine($"--ring {ringMode} widens the loaded references and --no-ring-refs takes them away; pass one.");
    return 2;
}
// A retained walk (--reuse-cell, on by default for a static worklist) is shared by every shot of a cell, and the visible
// set follows each shot's camera, so the two cannot be combined: refused rather than drawing shot 2 with shot 1's cells.
if (ringMode == "visible" && worklistPaths.Count > 0 && reuseCell && !interactiveMode)
{
    Console.Error.WriteLine("--ring visible chooses the loaded cells from each shot's camera, so a worklist cannot share one walk "
                          + "across its shots: pass --no-reuse-cell with it (every shot then walks the order for its own cells).");
    return 2;
}
if (reuseCellExplicit && noReuseCell)
{
    Console.Error.WriteLine("--reuse-cell and --no-reuse-cell contradict each other; pass one.");
    return 2;
}
// The lease is on for this run whenever it is a static worklist without --no-reuse-cell; such a run
// gets the same no-dialog failure boundary an explicit --reuse-cell always had.
if (reuseCell && worklistPaths.Count > 0 && !interactiveMode) __reuseFailureBoundary = true;
// Replication args: the command line verbatim, minus this machine's paths (instance/profile/
// game/out). Embedded in every output PNG — a replay from these is BYTE-exact, which the
// resolved camera pose alone is not: a --look camera replayed as the equivalent --yaw/--pitch
// composes the same view direction but a sub-pixel-different frame (measured).
// A WORKLIST SHOT does not use this: it names its own camera instead, because this string is the
// whole process's and every frame of a multi-shot run would otherwise claim the same command.
// See ReplayCommand.cs.
string replayArgs = ViewProbe.ReplayCommand.ForProcess(args);
bool batchOptionPresent = worklistSelectionPath is not null || progressDir is not null
    || worklistReadinessSha256 is not null;
if (worklistSelectionCount > 1 || progressDirCount > 1 || worklistReadinessCount > 1)
{
    Console.Error.WriteLine("worklist selection/progress operational flags may each appear only once.");
    return 2;
}
if (batchOptionPresent && (worklistSelectionPath is null || progressDir is null
    || worklistReadinessSha256 is null))
{
    Console.Error.WriteLine("--worklist-selection, --progress-dir and --worklist-readiness-sha256 must be supplied together.");
    return 2;
}
if (batchOptionPresent && (worklistPaths.Count != 1 || interactiveMode || emitBats is not null))
{
    Console.Error.WriteLine("worklist selection/progress requires exactly one worklist in static render mode (not --interactive or --emit-bats).");
    return 2;
}
if (worklistReadinessSha256 is not null && (worklistReadinessSha256.Length != 64
    || worklistReadinessSha256.Any(c => !Uri.IsHexDigit(c))))
{
    Console.Error.WriteLine("--worklist-readiness-sha256 requires exactly 64 hexadecimal characters.");
    return 2;
}
if (ViewProbe.CaptureViews.Protect.Count > 0 && ViewProbe.CaptureViews.Hide.Count == 0)
{ Console.Error.WriteLine("--protect only means anything with --hide: it refuses a hidden object that touches or holds up the protected one; add --hide or drop it."); return 2; }
if (ViewProbe.CaptureViews.CoverageDump && ViewProbe.CaptureViews.CoverageOf.Count == 0)
{ Console.Error.WriteLine("--coverage-dump only means anything with --coverage-of; add --coverage-of or drop it."); return 2; }
foreach (var cspec in ViewProbe.CaptureViews.CoverageOf)
{
    try { ViewProbe.CaptureViews.CoverageSpec(cspec); }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
}
if (refSurfaceOpacitySet && !refSurface)
{
    Console.Error.WriteLine("--ref-surface-opacity only means anything with --ref-surface: it sets the opacity of "
                          + "the exact placed-triangle subject overlay, which no other highlight mode draws.");
    Console.Error.WriteLine("  Add --ref-surface, or drop the opacity. It is refused rather than ignored so a "
                          + "capture cannot claim a treatment it did not render.");
    return 1;
}
if (depthDump is not null && (interactiveMode || compare is not null || worklistPaths.Count > 0))
{
    Console.Error.WriteLine("--depth-dump writes the depth buffer of ONE explicit-camera capture (--eye with --yaw/--pitch or --look). "
                          + "It is refused with --interactive, --compare and worklists, which render several frames or none.");
    return 2;
}

if (!drawNav) navMode = "off";
if ((conflictsPath is not null || redSpecs.Count > 0) && !navModeExplicit) navMode = "wire";
// What the command line settled on, kept so every worklist shot starts from it rather than from
// the previous shot's overrides.
string cliNavMode = navMode; float? cliCullAbove = cullAbove;
bool cliShowPlanes = showPlanes; string cliOcclusion = occlusionMode;
float cliCanvasAspect = canvasAspect;   // the run's canvas shape, before any shot overrides it
string? cliStateSpec = stateSpec;
if (navMode is not ("off" or "filled" or "wire" or "both"))
{ Console.Error.WriteLine($"--nav '{navMode}' is not one of: off, filled, wire, both."); return 1; }
if (occlusionMode is not ("off" or "on" or "diff"))
{ Console.Error.WriteLine($"--occlusion '{occlusionMode}' is not one of: off, on, diff."); return 1; }
// The point queries are answered inside the first-person pass (`if (textured)` below); a run without one answered none.
if (!textured && worklistPaths.Count == 0 && navAt.Count + clearAt.Count + insideAt.Count + refsAt.Count > 0)
{
    var pointOpt = navAt.Count > 0 ? "--nav-at" : clearAt.Count > 0 ? "--clear-at" : insideAt.Count > 0 ? "--inside-at" : "--refs-at";
    Console.Error.WriteLine($"{pointOpt} is answered only by the first-person pass, which this run does not make: add --eye X,Y,Z or --eye-ref FORMID (--size 64 --msaa 0 --nav off keeps that render cheap).");
    return 2;
}
if (!planesReport && Array.IndexOf(args, "--planes-reach") >= 0)
{ Console.Error.WriteLine("--planes-reach only means anything with --planes-report: it is how far behind each plane the report looks for solid collision."); return 1; }
if (Array.IndexOf(args, "--visibility-budget") >= 0 && (!planesReport || interactiveMode))
{ Console.Error.WriteLine("--visibility-budget requires --planes-report in a non-interactive view."); return 1; }
if (planesReach <= 0f)
{ Console.Error.WriteLine($"--planes-reach {planesReach} must be a positive number of units."); return 1; }
if (npcPuck is not ("on" or "off" or "auto"))
{ Console.Error.WriteLine($"--npc-puck '{npcPuck}' is not one of: on, off, auto."); return 1; }
// A storey filter over an overlay that is not drawn is a flag doing nothing, same trap as below.
if ((navZ is not null || navZAuto) && navMode == "off")
{ Console.Error.WriteLine("--nav-z only means anything with a navmesh overlay: --nav is off, so there is nothing to filter to a storey."); return 1; }
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
var routeNames = new Dictionary<string, System.Numerics.Vector3>(StringComparer.OrdinalIgnoreCase)
{
    ["cyan"] = new(0.05f, 0.85f, 0.95f),
    ["magenta"] = new(0.90f, 0.20f, 0.95f),
    ["yellow"] = new(1.00f, 0.88f, 0.10f),
    ["green"] = new(0.05f, 0.85f, 0.30f),
    ["blue"] = new(0.25f, 0.45f, 1.00f),
    ["pink"] = new(1.00f, 0.40f, 0.80f),
};
if (routeColor == default)
{
    if (!routeNames.TryGetValue(routeColorName, out var rcv))
    {
        Console.Error.WriteLine($"--route-color '{routeColorName}' is not one of: {string.Join(", ", routeNames.Keys)} — or an R,G,B triple (0-255 or 0-1). "
                              + "Red, orange, violet and white are reserved for conflicts, doors, nav diffs and the cell outline.");
        return 1;
    }
    routeColor = new System.Numerics.Vector4(rcv, 1f);
}
// A worklist route's own colour, by the same rule as --route-color; absent inherits it.
bool TryRouteColour(string? spec, out System.Numerics.Vector4 colour, out string error)
{
    colour = routeColor; error = "";
    if (string.IsNullOrWhiteSpace(spec)) return true;
    var parts = spec.Split(',');
    if (parts.Length == 3 && float.TryParse(parts[0], out var cr) && float.TryParse(parts[1], out var cg) && float.TryParse(parts[2], out var cb))
    {
        var v = new System.Numerics.Vector3(cr, cg, cb);
        if (v.X > 1f || v.Y > 1f || v.Z > 1f) v /= 255f;
        colour = new System.Numerics.Vector4(v, 1f);
        return true;
    }
    if (routeNames.TryGetValue(spec.Trim(), out var named)) { colour = new System.Numerics.Vector4(named, 1f); return true; }
    error = $"colour '{spec}' is not one of: {string.Join(", ", routeNames.Keys)} — or an R,G,B triple (0-255 or 0-1).";
    return false;
}
// The grid's named colours. Default is a warm off-white GREY, which is deliberate: a grid is the
// paper the picture is drawn on, not a thing in it, and the five saturated palette entries all mean
// something already. Whatever is chosen becomes reserved — the nav wire's auto-pick avoids it.
if (gridColor == default)
{
    var gridNames = new Dictionary<string, System.Numerics.Vector3>(StringComparer.OrdinalIgnoreCase)
    {
        ["grey"] = new(0.82f, 0.80f, 0.74f),
        ["gray"] = new(0.82f, 0.80f, 0.74f),
        ["cyan"] = new(0.05f, 0.85f, 0.95f),
        ["magenta"] = new(0.90f, 0.20f, 0.95f),
        ["yellow"] = new(1.00f, 0.88f, 0.10f),
        ["green"] = new(0.05f, 0.85f, 0.30f),
        ["blue"] = new(0.25f, 0.45f, 1.00f),
        ["pink"] = new(1.00f, 0.40f, 0.80f),
    };
    if (!gridNames.TryGetValue(gridColorName, out var gcv))
    {
        Console.Error.WriteLine($"--grid-color '{gridColorName}' is not one of: {string.Join(", ", gridNames.Keys)} — or an R,G,B triple (0-255 or 0-1). "
                              + "Red, orange, violet and white are reserved for conflicts, doors, nav diffs and the cell outline.");
        return 1;
    }
    gridColor = new System.Numerics.Vector4(gcv, 1f);
}
// A flag that silently does nothing is a trap — the same rule --fly-speed and --route-color are held to.
if (!gridOn && Array.IndexOf(args, "--grid-color") >= 0)
{ Console.Error.WriteLine("--grid-color only means anything with a grid: pass --grid N or --grid auto."); return 1; }
if (waterArm is not ("winner" or "truth"))
{ Console.Error.WriteLine($"--water-arm {waterArm}: the arms are 'winner' (the load order's tuple, the default) and 'truth' (the last Bethesda/USSEP statement)."); return 1; }
// The window already answers "what world coordinate is this" continuously, by hovering: the HUD
// prints it. What it cannot carry is the grid's LABELS, which are screen positions computed for one
// fixed pose and wrong the instant the camera moves — so this is a static-render flag, and says so
// rather than opening a window with an unlabelled hatch in it.
if (gridOn && interactiveMode)
{ Console.Error.WriteLine("--grid is a static-render flag: its labels are computed for ONE camera pose and would be wrong as soon as you fly. In the window, hover instead — the HUD names the world coordinate under the cursor. Fly first, then render the grid from a pose the window printed."); return 1; }
if (routeArrowsSpec == "auto") routeArrowEvery = null;
else if (routeArrowsSpec == "end") routeArrowEvery = 0f;
else if (float.TryParse(routeArrowsSpec, out var rae) && rae > 0f) routeArrowEvery = rae;
else
{ Console.Error.WriteLine($"--route-arrows '{routeArrowsSpec}' is not auto, end, or a positive number of world units."); return 1; }
if (routeXY.Count == 1)
{ Console.Error.WriteLine("--route needs at least two waypoints: one point is a position, not a path."); return 1; }
// A flag that silently does nothing is a trap — the same rule --fly-speed is held to. --route-z is
// the exception in the family: it is no longer the route's alone. --nav-z auto and --grid both take
// their storey from it, so with either of those asking it IS doing something, and refusing it there
// would make "state the floor once, use it for the filter, the grid and the path" impossible.
bool routeZServesSomethingElse = gridOn || navZAuto;
if (routeXY.Count == 0 && worklistPaths.Count == 0
    && (Array.IndexOf(args, "--route-color") >= 0 || Array.IndexOf(args, "--route-arrows") >= 0
        || (Array.IndexOf(args, "--route-z") >= 0 && !routeZServesSomethingElse)
        || Array.IndexOf(args, "--no-route-discs") >= 0 || Array.IndexOf(args, "--route-solve") >= 0))
{ Console.Error.WriteLine("--route-color / --route-z / --route-arrows / --no-route-discs / --route-solve only mean anything with a route: pass --route X,Y (repeatable) or a worklist whose shots carry one. (--route-z alone is accepted with --grid or --nav-z auto, which take the storey from it.)"); return 1; }
if (emitBats is not null && worklistPaths.Count == 0)
{ Console.Error.WriteLine("--emit-bats needs --worklist: the bat files ARE the worklist, written for the console instead of the camera."); return 1; }
if (emitBats is not null && worklistPaths.Count > 1)
{ Console.Error.WriteLine("--emit-bats takes ONE worklist: the bat mod's MANIFEST.md names the file it was generated from, and a merge of several has no such file. Emit them one at a time."); return 1; }
if (batsProfiles.Count > 0 && emitBats is null)
{ Console.Error.WriteLine("--bats-profiles only means anything with --emit-bats: it enables the mod folder that --emit-bats writes."); return 1; }
if ((dumpCellsFile is not null || dumpTerrain) && winRefsDump is null)
{ Console.Error.WriteLine("--cells and --dump-terrain only mean anything with --winning-refs-dump FILE."); return 1; }
if (winRefsDump is not null && (worklistPaths.Count > 0 || (dumpCellsFile is not null && positionalTarget)))
{ Console.Error.WriteLine("--winning-refs-dump takes its cells from ONE place: a positional cell, or --cells FILE; not a worklist."); return 1; }
if (dumpTerrain && (!textured || !drawTerrain))
{ Console.Error.WriteLine("--dump-terrain needs the textured renderer's terrain walk: give --eye and --look, and not --no-terrain."); return 1; }
if (!positionalTarget && worklistPaths.Count == 0 && !(winRefsDump is not null && dumpCellsFile is not null))
{ Console.Error.WriteLine("no cell given. Pass a CellEditorID/FormID as the first argument, or --worklist FILE to take the cells from a shot list."); return 1; }

// The worklist is read and VALIDATED before the load walk. A malformed shot list found after a
// multi-minute walk is a multi-minute walk wasted.
HarnessShared.WorklistFile? worklist = null;
ViewProbe.BatchProgress? batchProgress = null;
if (worklistPaths.Count > 0)
{
    var loaded = new List<(string Path, HarnessShared.WorklistFile File)>();
    foreach (var wp in worklistPaths)
    {
        try { loaded.Add((wp, HarnessShared.WorklistIo.Read(wp))); }
        catch (Exception ex) { Console.Error.WriteLine(batchOptionPresent ? ex.ToString() : ex.Message); return batchOptionPresent ? 2 : 1; }
    }
    try { worklist = HarnessShared.WorklistIo.Merge(loaded); }
    catch (Exception ex) { Console.Error.WriteLine(batchOptionPresent ? ex.ToString() : ex.Message); return batchOptionPresent ? 2 : 1; }

    string src = loaded.Count == 1
        ? worklistPaths[0]
        : $"{loaded.Count} files: {string.Join(", ", loaded.Select(l => Path.GetFileName(l.Path)))}";
    Console.WriteLine($"worklist '{worklist.Label}': {worklist.Cells.Count} cell(s), "
                    + $"{worklist.Cells.Sum(c => c.Shots.Count)} shot(s)  [{src}]");
    if (batchOptionPresent)
    {
        try
        {
            batchProgress = ViewProbe.BatchProgress.Load(worklistSelectionPath!, progressDir!,
                worklistPaths[0], worklistReadinessSha256!,
                worklist.Cells.Select(c => c.Shots.Count).ToArray());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("worklist selection/progress refused:\n" + ex);
            return 2;
        }
    }
}
// How many shots this run renders, so a reader of the record knows how many times the per-shot
// stages are expected to repeat. One when there is no worklist.
ViewProbe.RenderStages.Feature("shots", batchProgress?.SelectedCount
    ?? worklist?.Cells.Sum(c => c.Shots.Count) ?? 1, "shots");

// A DROP IS A HUMAN AT A MOUSE. Static mode writes numbered PNGs into a folder they would then have
// to go and find, and pays the load walk once per SHOT; the fly-through pays it once per CELL and
// puts the scene on a screen, which is what "open this" means when the gesture was dragging a file
// onto an icon. Only the GUI launch is touched — a shell run, with or without --interactive, keeps
// the behaviour it has always had.
if (guiLaunch && worklist is not null && !interactiveMode && batchProgress is null)
{
    interactiveMode = true;
    textured = true;
    Console.WriteLine("launched from Explorer — opening the fly-through: PageUp/PageDown steps the shots, "
                    + "Ctrl+PageUp/PageDown steps the cells.");
    Console.WriteLine("(run it from a shell without --interactive to render every shot to a PNG instead.)");
}
if (keepAllMasks && !depthMeasurement)
{ Console.Error.WriteLine("--keep-all-masks only means anything with --depth-measurement, since without it every mask is already written; add --depth-measurement or drop it."); return 1; }
if (occlusionQuery && !depthMeasurement)
{ Console.Error.WriteLine("--occlusion-query only applies with --depth-measurement, whose isolated draws it counts; add --depth-measurement or drop the flag."); return 1; }
if (depthMeasurement && !planesReport)
{ Console.Error.WriteLine("--depth-measurement only applies to a --planes-report run; add --planes-report or drop the flag."); return 1; }
if (depthMeasurement && stationsSpec is not null)
{ Console.Error.WriteLine("--depth-measurement does nothing in --stations mode, which renders no planes report; drop the flag."); return 1; }
if (depthMeasurement && interactiveMode)
{ Console.Error.WriteLine("--depth-measurement is refused in the interactive window, which draws the textured scene for a person; render the planes report without --interactive."); return 1; }
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
Directory.CreateDirectory(outDir);

// ---------------------------------------------------------------------------------------
// 1. Resolve the cell's winning state
// ---------------------------------------------------------------------------------------
var sw = Stopwatch.StartNew();
var comp = Mo2LoadOrder.ReadComposition(profileDir);
// Ordered BEFORE archive discovery so --emit-bats, which reads records and no assets at all, never
// pays for BSA indexing it has no use for.
var order = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir);
HarnessShared.ResolvedConfiguration? candidateConfiguration = null;
string? candidateSha256 = null;
if (orderFile is not null)
{
    try
    {
        // Hold the candidate read-only through hashing and resolution. A later edit must not
        // replace the receipt's hash with bytes that did not supply this capture's inputs.
        using var candidateSource = File.Open(orderFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        candidateSha256 = HarnessShared.ConfigurationIdentity.Sha256File(orderFile);
        candidateConfiguration = HarnessShared.HarnessOrderCli.Resolve(profileName, profileDir, modsDir,
            dataDir, overwriteDir, GamePath, orderFile, null, null);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"candidate file refused: {orderFile}: {ex.Message}");
        return 2;
    }
    if (candidateConfiguration.Refused)
    { Console.Error.WriteLine(candidateConfiguration.RefusedReason); return 2; }
    order = new Mo2OrderResult(candidateConfiguration.OrderedPaths, candidateConfiguration.Warnings,
                              candidateConfiguration.OrderedPaths.Count);
    HarnessShared.HarnessOrderCli.Print(candidateConfiguration);
}
// A profile that lists an active plugin no file provides: refused, as --order already refuses (PluginRead.cs). The
// plugin used to drop out of the order silently, leaving an earlier plugin's version of everything it overrides.
if (candidateConfiguration is null && order.OrderedPaths.Count < order.ActiveCount)
{
    foreach (var w in order.Warnings.Where(w => w.StartsWith("load order lists", StringComparison.Ordinal)))
        Console.Error.WriteLine("   " + w);
    Console.Error.WriteLine($"the profile's load order has {order.ActiveCount} active plugin(s) and only {order.OrderedPaths.Count} "
                          + "resolve to a file; refusing: without the missing ones, an earlier plugin's version of every record "
                          + "they override would be used silently.");
    return 2;
}
// A profile plugin whose master is not loaded: refused, as --order refuses it (ruling 12; _shared/OrderMasters.cs).
if (candidateConfiguration is null
    && HarnessShared.OrderMasters.RefusalForProfile(profileDir, modsDir, dataDir, overwriteDir, order.OrderedPaths) is { } missingMaster)
{
    Console.Error.WriteLine(missingMaster);
    return 2;
}
// The configuration this capture REPORTS, which is not always the one it was built from:
// --order REPLACES the configuration, while --config-identity only asks what the profile's own
// inputs were. Kept in its own variable so asking the question cannot change the order, the asset
// view or the archives this run draws from — a reporting flag that moved the picture would be
// worse than no flag at all.
HarnessShared.ResolvedConfiguration? reportedConfiguration = candidateConfiguration;
if (reportedConfiguration is null && configIdentity)
{
    var resolved = HarnessShared.HarnessOrderCli.Resolve(profileName, profileDir, modsDir,
        dataDir, overwriteDir, GamePath, null, null, null);
    if (resolved.Refused)
        Console.Error.WriteLine($"--config-identity: {resolved.RefusedReason} — the capture "
                              + "proceeds and reports NO configuration identity.");
    else
    {
        reportedConfiguration = resolved;
        HarnessShared.HarnessOrderCli.Print(resolved);
    }
}
// WHAT ran, as an opaque id, and only when this run had already resolved one: asking for a
// configuration costs a resolve, and a record is not a reason to pay it. The fingerprint is a hash
// and names no plugin, which is the form the contract permits for an input (§11).
ViewProbe.RenderStages.Configuration(
    reportedConfiguration?.Identity("load-time", stateSpec ?? "baseline").Fingerprint);
var assetMods = candidateConfiguration?.EnabledMods ?? comp.EnabledMods;

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
    // P50: the bat files are played in the real game, which shows the launched profile's assets. A candidate with its own
    // Mods order is refused for any named profile that does not hold that order; without a named profile, say so.
    if (orderFile is not null && HarnessShared.CandidateFile.Read(orderFile).Mods is { } candidateMods)
    {
        if (batsProfiles.Count == 0)
            Console.WriteLine("emit-bats: the game shows the assets of the profile you launch; to see this candidate's view, launch a profile that enables its Mods in the candidate's order");
        foreach (var batsProfile in batsProfiles)
            if (ViewProbe.BatEmit.ProfilePriorityRefusal(Instance, batsProfile, candidateMods) is { } refusal)
            { Console.Error.WriteLine("--emit-bats refused: " + refusal + "."); return 2; }
    }
    try { return ViewProbe.BatEmit.Run(worklist!, worklistPaths[0], batsDir, batsName, order.OrderedPaths, Instance, batsProfiles); }
    catch (ViewProbe.ReadRefusal ex) { ViewProbe.PluginRead.Report(ex); return 2; }   // PluginRead.cs
}

var arch = candidateConfiguration?.Archives ?? ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, assetMods, arch.Archives);

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
// --state: every ref's editor id (the switches are named by it), kept only when a state is asked for.
var refEditorIdAll = new Dictionary<FormKey, string>();
// Occlusion-plane provenance: every plugin's version of every PlaneMarker ref, in load order (a few
// thousand entries at most, so the whole order is recorded rather than only the target's cells —
// a persistent plane merges into the scene by position, after the walk has decided its cell). The
// plugins that contribute refs to the TARGET cell and are not a base master make it "contested".
var planeMarkerKey = new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x17);
var planeHist = new Dictionary<FormKey, ViewProbe.PlaneHistory>();
var cellContestedBy = new List<string>();
// --npc placement watch, same shape as the door watch: every PlacedNpc whose own FormKey OR
// whose Base is a requested NPC, wherever it lives (per-ref "later plugin wins").
var npcRefWhere = new Dictionary<FormKey, (FormKey Cell, string? CellEd, PlacedRef Ref)>();
// References whose last sighting is a deletion with no placement data (K6), and the plugin that deleted them: the
// "where is it" tables above drop them, and a watched door or NPC is reported as deleted rather than at an earlier spot.
var refDeletedBy = new Dictionary<FormKey, string>();
var npcBaseWhere = new Dictionary<FormKey, Dictionary<FormKey, (FormKey Cell, string? CellEd, PlacedRef Ref)>>();
var whereTables = new ViewProbe.RefWhere.Tables(refDeletedBy, npcRefWhere, npcBaseWhere, doorWhere);   // filled by RefWhere.Observe
var winNav = new Dictionary<FormKey, NavmeshGeometry>();
FormKey targetWorldspace = FormKey.Null;
(int X, int Y)? targetGrid = null;
// Record versions per plugin, collected only for the plugins a panel needs.
var navVersionPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
if (navPlugin is not null) navVersionPlugins.Add(navPlugin);
if (compare is { } cmp0) { navVersionPlugins.Add(cmp0.A); navVersionPlugins.Add(cmp0.B); }
var cliNavVersionPlugins = navVersionPlugins.ToArray();
var navByPlugin = new Dictionary<string, Dictionary<FormKey, NavmeshGeometry>>(StringComparer.OrdinalIgnoreCase);
ViewProbe.NavmeshSelection? navSelection = null;
ViewProbe.NavmeshLinks? navLinks = null;
if (navVersionSpecs.Count > 0 && (navPlugin is not null || compare is not null))
{ Console.Error.WriteLine("--nav-version cannot be combined with --nav-plugin or --compare."); return 2; }
var baseMesh = new Dictionary<FormKey, string>();
// The Base Object Swapper reader's inputs, filled once during the load-order walk below (the same pass that
// collects meshes): the form index the ini ids resolve against, each plugin's light flag for the runtime FormID
// table, and each winning ref's cell for the filters. The layer itself is loaded after the first walk.
var bosIdx = bosMode != "off" ? new BosFormIndex() : null;
var bosLight = new List<(ModKey Key, bool Light)>();
var refCellOf = new Dictionary<FormKey, FormKey>();
var refOutcome = new Dictionary<FormKey, (string Outcome, string? BaseEditorId)>();
// Where each ref left out as a marker or for having no model stands, for a host that draws them as gizmos.
var refUndrawnAt = new Dictionary<FormKey, (Vec3 Position, Vec3 Rotation)>();
var winWalk = new ViewProbe.WinningRefWalk(winRef, persistentWin, refCellOf);
// The ring edge (WinningRefs.cs): references the game loads two cells out whose bounds enter the 3x3, per shot.
int edgeAdmittedCount = 0, edgePlanesAdmittedCount = 0;
// Large references (WinningRefs.cs): listed references anchored 3-5 cells out whose bounds enter the 3x3, per shot.
int largeAdmittedCount = 0, largePlanesAdmittedCount = 0, largeListedCount = 0;
bool largeIsChild = false;
BosLayer? bosLayer = null;
IReadOnlyList<BosIniFile>? discoveredBosInis = null;
var baseIsMarker = new HashSet<FormKey>();
var baseEditorId = new Dictionary<FormKey, string>();
// Base record TYPE and OBND, for --refs-at (WHAT is near a spot: a Door, a Container) and
// --inside-at (whose box a point is in). Modeled bases only — the set baseMesh covers —
// winner-takes-all in load order like everything here.
var baseType = new Dictionary<FormKey, string>();
var baseObnd = new Dictionary<FormKey, (Vec3 Min, Vec3 Max)>();
// Model-space render soups for the --inside-at ray test, by mesh path (the flat build keeps its
// own cache further down; this one is filled only for the few refs whose box encloses a query).
var insideMesh = new Dictionary<string, (Aabb? Bounds, TriangleSoup? Soup)>(StringComparer.OrdinalIgnoreCase);
// Per soup triangle, how its face may be judged (the --inside-at facing): 0 one-sided (the renderer culls its back),
// 1 two-sided (drawn both ways), 2 unknown (its shape's node chain mirrors it, which flips the winding; or no shape map).
var insideFaceKind = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
var insideEffectOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var insideNoSolidShape = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var ltexToTxst = new Dictionary<FormKey, FormKey>();
var ltexEditorId = new Dictionary<FormKey, string>();
var txstDiffuse = new Dictionary<FormKey, string>();
var txstNormal = new Dictionary<FormKey, string>();

// ---- projected coverings (MATO) and water, both winner-takes-all over the load-order walk ----
// Values are COPIED out of the record getters, never referenced: every mod overlay is opened in a
// `using` and disposed at the end of its iteration, so a retained getter is a use-after-free that
// happens to work until it doesn't.
var baseMato = new Dictionary<FormKey, (FormKey Mato, float MaxAngle)>();
var matoRec = new Dictionary<FormKey, (string Ed, System.Numerics.Vector3 Projection, float Bias, float Scale,
                                       float UvScale, System.Numerics.Vector3 Color, bool SinglePass, string? Model,
                                       float NoiseUv)>();
var watrRec = new Dictionary<FormKey, (string Ed, string? Noise, float NoiseUv,
                                       System.Numerics.Vector3 Shallow, System.Numerics.Vector3 Deep,
                                       System.Numerics.Vector3 Reflect, float Reflectivity,
                                       byte Opacity, float Fresnel,
                                       float FogNear, float FogFar, float FogAmount, float NoiseFalloff,
                                       System.Numerics.Vector3 Spec)>();   // (sun specular power, sun specular magnitude, depth specular lighting)
// Per-cell water, keyed by grid point, plus the worldspace fallbacks the cells defer to.
var cellWater = new Dictionary<(int X, int Y), (bool Has, float Height, FormKey? Water)>();
// --stations only. WHICH cell each grid square is (the pose names the cell the STATION stands in,
// and the in-game driver verifies it by formId), every worldspace's own default water height and
// its parent link (a city worldspace commonly takes its water from Tamriel), and the floor
// neighbourhood filed by grid the way navprobe files it. All three cost nothing when the mode is
// off: nothing fills them.
var cellAtGrid = new Dictionary<(int X, int Y), (FormKey Key, string? Ed)>();
var worldChain = new Dictionary<string, (float? Default, string? Parent)>(StringComparer.OrdinalIgnoreCase);
string stationWorldspaceEd = "";
var stationRefsByGrid = new Dictionary<(int, int), Dictionary<FormKey, PlacedRef>>();
var stationLandByGrid = new Dictionary<(int, int), float[,]>();
var stationUnreadableLand = new Dictionary<(int, int), string>();   // grids whose last LAND gives no heights (FloorIndex)
FloorFeed.FloorReach? stationReach = null;   // the station floor's reach beyond the 3x3 (FloorFeed.FloorReach)
// How far out the cells' water and identity are harvested. One ring is all the renderer needs; a
// station reaches up to the standoff cap plus a lateral from its primitive, which can leave the
// target cell, so the mode widens it and the floor is filed one square wider still (FloorFeed
// reads the 3x3 around the station's own square).
int stationCellRadius = stationsSpec is not null ? 3 : 1;
const int StationFloorRadius = 4;
FormKey? wsWater = null;
float wsDefaultWaterHeight = float.MaxValue;
// The TRUTH arm of the same three (--water-arm truth): the last Bethesda-or-USSEP statement, by filename
// (HarnessShared.PluginKinds) — waterprobe's truth rule without its water-mod clause, which needs the whole
// order's WATR records and is not worth a second walk here; the console says so when the arms differ.
var cellWaterTruth = new Dictionary<(int X, int Y), (bool Has, float Height, FormKey? Water)>();
FormKey? wsWaterTruth = null;
float wsDefaultWaterHeightTruth = float.MaxValue;

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
// The chain rule itself is EnableState.Resolve in housecarl-core (shared with the placement-contact
// index since 2026-09-03); the facts here are the winner-takes-all flag and XESP maps above, given
// for EVERY key — a ref with no entry is flag-enabled and parentless — so the answers are the ones
// this file's own resolver gave before the move.
var enableMemo = new Dictionary<FormKey, EnableResolution>();
EnableFacts? EnableFactsOf(FormKey fk) =>
    new EnableFacts(refDisabledFlag.Contains(fk),
                    refEnableParent.TryGetValue(fk, out var p) ? p.Parent : null,
                    refEnableParent.TryGetValue(fk, out var q) && q.Opposite);
// --state: the facts the scene resolves through — the records' own until a state arm wraps them after the walk
// (a flipped switch reads as the opposite of its load-time state, its children follow through the chain rule).
Func<FormKey, EnableFacts?> activeFacts = EnableFactsOf;
var stateBaselineMemo = new Dictionary<FormKey, EnableResolution>();
HarnessShared.StateArm? stateArm = null;
bool RefEnabled(FormKey fk) => EnableState.Resolve(fk, activeFacts, enableMemo).Enabled;
bool RefEnabledBaseline(FormKey fk) => EnableState.Resolve(fk, EnableFactsOf, stateBaselineMemo).Enabled;
// The three dispositions a drop report needs to tell apart.
string? DisabledWhy(PlacedRef r) =>
    RefEnabled(r.FormKey) ? null
    : refEnableParent.TryGetValue(r.FormKey, out var dp) ? $"disabled via enable parent {dp.Parent}"
    : "initially disabled";
// The base types a camera can be INSIDE (--inside-at): the placement-contact rule's physical
// hosts plus the solid dependents, minus items and lights — a light's OBND is its radius, and
// a camera is "inside" every light in the room. A literal set rather than DetectorModel's
// classifiers: viewprobe compiles without them, and "can a camera be in this" is a narrower
// question than "can this hold something". Control objects (markers, triggers) are the
// same exclusions the rule makes. An "FX" editor id is NOT one any more (2026-11-22, task
// 2026-11-11-viewprobe-fx-editor-id-rule): it was a name rule like the effects\ path rule, and it
// hid lit solids the base game names FX (snow drifts, creek and rapids rocks, Sky Haven's
// temple). What is not solid in such a mesh is decided per shape, as for every mesh (InsideMeshFor:
// effect, hidden and refraction shapes), so an effect-only FX base is still never solid.
var insidePhysical = new HashSet<string> { "Static", "MoveableStatic", "Furniture", "Container", "Door", "Activator", "Tree", "Flora" };
bool IsControlEditorId(string ed) =>
    ed.Contains("marker", StringComparison.OrdinalIgnoreCase) || ed.Contains("trigger", StringComparison.OrdinalIgnoreCase);
// An OBND box cannot tell a bookcase from a room: a tower-kit wall piece's box (966x966x384 u,
// measured 2026-09-03) encloses every camera in the room it walls, exactly as a bookcase's box
// encloses a camera on its shelf. So the box only NOMINATES; the ref's own render mesh decides.
// Six world-axis rays from the point against that mesh: a shelf above and below and a back panel
// behind all hit within arm's reach, a room's walls do not. The distances are printed per axis
// and the caller applies its reach and count; a mesh that cannot be read is said, and the box
// stands alone as the weaker answer.
(Aabb? Bounds, TriangleSoup? Soup) InsideMeshFor(string rel)
{
    if (insideMesh.TryGetValue(rel, out var cached)) return cached;
    (Aabb? Bounds, TriangleSoup? Soup) got = (null, null);
    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
    var res = assets.ResolveForPlacement(full);
    if (res.Sources.Count > 0)
    {
        var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
        if (bytes is not null)
        {
            var o = NifGeometry.Measure(bytes, includeTriangles: true);
            // The BOUNDS survive the triangle cap: a mesh too big to hold as a soup can still say
            // how far it reaches, and that is exactly what the broad phase needs. Keeping the two
            // apart is what lets an over-cap ref be reported as UNREADABLE for the ray test while
            // still being admitted as a candidate rather than silently dropped.
            var b = o.Model?.Bounds;
            got = (b is { IsEmpty: false } ? b : null,
                   o.Model?.Soup is { TriangleCount: > 0 and <= 300_000 } sp ? sp : null);
            // A mesh that READ but holds no triangles at all (FXEmptyObject.nif, the dragon dust emitters) has nothing a
            // ray can meet: no solid shape, not "could not be read" (2026-11-22, when the FX editor-id rule went).
            if (o.Model is { } m0 && (m0.Soup is null || m0.Soup.TriangleCount == 0) && got.Bounds is null)
                insideNoSolidShape.Add(rel);
            if (got.Soup is { } gs && o.Model is { SoupShapeOfTriangle: { } map, Shapes: { } shapes } && map.Count == gs.TriangleCount)
            {
                // A shape is solid by its own data. Not solid: an effect-shader shape (cloud, glow card, flame, editor
                // marker); a shape hidden by APP_CULLED on itself or its node chain (the game does not draw it); a lit
                // shape whose shader has the Refraction flag (heat haze, a screen distortion, not a surface). Their
                // triangles leave the soup these queries test; drawing is unchanged. A shape with no shader stays solid.
                var refraction = RefractionShapeBlocks(bytes);
                bool IsEffect(int si) => si >= 0 && si < shapes.Count && shapes[si].Effect;
                bool NotSolid(int si) => si >= 0 && si < shapes.Count
                                         && (shapes[si].Effect || shapes[si].Hidden || refraction.Contains(shapes[si].BlockIndex));
                var keep = Enumerable.Range(0, map.Count).Where(t => !NotSolid(map[t])).ToArray();
                if (keep.Length < map.Count)
                {
                    if (keep.Length == 0)
                    {
                        // "effect-only" keeps its meaning (every left-out triangle is an effect shape's); otherwise the
                        // mesh has no solid shape for another reason, said separately.
                        if (Enumerable.Range(0, map.Count).All(t => IsEffect(map[t]))) insideEffectOnly.Add(rel);
                        else insideNoSolidShape.Add(rel);
                        got = (got.Bounds, null);
                    }
                    else
                    {
                        var idx = keep.SelectMany(t => new[] { gs.Indices[t * 3], gs.Indices[t * 3 + 1], gs.Indices[t * 3 + 2] }).ToArray();
                        got = (got.Bounds, new TriangleSoup(gs.Vertices, idx,
                                                            gs.TriangleMaterials is { } tm ? keep.Select(t => tm[t]).ToArray() : null,
                                                            gs.TriangleRadii is { } tr ? keep.Select(t => tr[t]).ToArray() : null));
                    }
                }
                if (got.Soup is not null)
                    insideFaceKind[rel] = keep.Select(t => map[t]).Select(si => si < 0 || si >= shapes.Count || shapes[si].Mirrored ? (byte)2
                                                       : shapes[si].DoubleSided ? (byte)1 : (byte)0).ToArray();
            }
        }
    }
    insideMesh[rel] = got;
    return got;
}
TriangleSoup? InsideSoupFor(string rel) => InsideMeshFor(rel).Soup;
// A mesh every one of whose drawn shapes is an effect shader: never solid to --inside-at or --clear-at.
bool InsideEffectOnly(string rel) { InsideMeshFor(rel); return insideEffectOnly.Contains(rel); }
// A mesh with no solid shape, not all of them effect shapes (hidden or refraction shapes among them): never solid either.
bool InsideNoSolidShape(string rel) { InsideMeshFor(rel); return insideNoSolidShape.Contains(rel); }
// Block indices of the shapes whose shader is a BSLightingShaderProperty with SLSF1 Refraction set. Read here with
// NiflySharp (as housecarl-core's NifGeometry and SurfaceTexcoords read shaders); matched to NifShapeGeometry.BlockIndex.
// A mesh nifly cannot load yields none, so nothing is dropped on a guess.
static HashSet<int> RefractionShapeBlocks(byte[] bytes)
{
    var set = new HashSet<int>();
    var nif = new NiflySharp.NifFile();
    try
    {
        using var ms = new MemoryStream(bytes, writable: false);
        if (nif.Load(ms) != 0) return set;
        for (int i = 0; i < nif.Blocks.Count; i++)
            if (nif.Blocks[i] is NiflySharp.INiShape s && nif.GetShader(s) is NiflySharp.Blocks.BSLightingShaderProperty ls
                && ls.ShaderFlags_SSPF1.HasFlag(NiflySharp.Enums.SkyrimShaderPropertyFlags1.Refraction))
                set.Add(i);
    }
    catch (Exception) { set.Clear(); }
    return set;
}
// A FLOOR candidate's solid, for --stations. Not InsideMeshFor's soup: that is the RENDER mesh,
// and a floor is what an actor stands on — the collision soup on the actor-solid layers, with ITS
// own box for the broad phase. navprobe's Geometry(), restated here over viewprobe's own resolver,
// including its three outcomes: none (no actor-solid body — the object is not a floor), collision
// (the complete solid), fallback (the decode was incomplete, so the render soup stands in).
var floorGeo = new Dictionary<string, (Aabb Box, TriangleSoup Soup)?>(StringComparer.OrdinalIgnoreCase);
(Aabb Box, TriangleSoup Soup)? FloorSolidFor(string rel)
{
    if (floorGeo.TryGetValue(rel, out var hit)) return hit;
    (Aabb Box, TriangleSoup Soup)? got = null;
    var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
    var res = assets.ResolveForPlacement(full);
    if (res.Sources.Count > 0)
    {
        var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
        if (bytes is not null && NifGeometry.Measure(bytes, includeTriangles: true).Model is { } m)
        {
            bool anySolid = m.CollisionBodies is not null
                && m.CollisionBodies.Any(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
            var solid = m.HasCollision && anySolid
                ? m.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer)) : null;
            if (m.HasCollision && anySolid && m.CollisionComplete && solid is { TriangleCount: > 0 })
            {
                var box = Aabb.Empty;
                foreach (var v in solid.Vertices) box = box.Add(v);
                if (!box.IsEmpty) got = (box, solid);
            }
            else if (m.HasCollision && anySolid && m.Soup is { TriangleCount: > 0 and <= 300_000 } rs
                     && !m.Bounds.IsEmpty)
                got = (m.Bounds, rs);          // fallback: an incomplete decode, labelled by this branch
        }
    }
    floorGeo[rel] = got;
    return got;
}
(Aabb Box, TriangleSoup Soup)? StationFloorGeometry(PlacedRef r)
    => r.Base is { } b && baseMesh.TryGetValue(b, out var rel) ? FloorSolidFor(rel) : null;
// Nearest hit along a ray against a world-space soup (Möller–Trumbore, both faces), or null
// when nothing is hit within `limit`.
static float? NearestHit(Vector3 o, Vector3 d, Vector3[] v, int[] idx, float limit)
{
    float? best = null;
    for (int i = 0; i + 2 < idx.Length; i += 3)
    {
        var a = v[idx[i]];
        var e1 = v[idx[i + 1]] - a;
        var e2 = v[idx[i + 2]] - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-6f) continue;
        float inv = 1f / det;
        var s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) continue;
        var q = Vector3.Cross(s, e1);
        float w = Vector3.Dot(d, q) * inv;
        if (w < 0f || u + w > 1f) continue;
        float t = Vector3.Dot(e2, q) * inv;
        if (t > 1e-3f && t <= limit && (best is null || t < best)) best = t;
    }
    return best;
}
// Nearest hit and the triangle it was on (NearestHit's test), or null.
static (float T, int Tri)? NearestHitTri(Vector3 o, Vector3 d, Vector3[] v, int[] idx, float limit)
{
    (float T, int Tri)? best = null;
    for (int i = 0; i + 2 < idx.Length; i += 3)
    {
        var a = v[idx[i]];
        var e1 = v[idx[i + 1]] - a;
        var e2 = v[idx[i + 2]] - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-6f) continue;
        float inv = 1f / det;
        var s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) continue;
        var q = Vector3.Cross(s, e1);
        float w = Vector3.Dot(d, q) * inv;
        if (w < 0f || u + w > 1f) continue;
        float t = Vector3.Dot(e2, q) * inv;
        if (t > 1e-3f && t <= limit && (best is null || t < best.Value.T)) best = (t, i / 3);
    }
    return best;
}
// Which way the face a ray met is turned: `back` when its normal (counter-clockwise winding, the face the renderer
// keeps; back faces are culled) points along the ray, so the eye is BEHIND that surface and would see out through it;
// `front` when it faces the eye; `two` for a two-sided shape, drawn either way; `-` when the facing cannot be told
// (a mirrored node chain flips the winding, or the shape map is missing) or nothing was hit. From inside a closed
// solid every ray meets a back face, at any thickness.
static string Facing(Vector3 d, Vector3[] v, int[] idx, int tri, byte[]? kinds)
{
    byte k = kinds is not null && tri < kinds.Length ? kinds[tri] : (byte)2;
    if (k == 2) return "-";
    if (k == 1) return "two";
    var a = v[idx[tri * 3]];
    var n = Vector3.Cross(v[idx[tri * 3 + 1]] - a, v[idx[tri * 3 + 2]] - a);
    float dn = Vector3.Dot(n, d);
    return dn > 0f ? "back" : dn < 0f ? "front" : "-";
}
string InsideRays(PlacedRef r, string mesh, float qx, float qy, float qz)
{
    if (InsideSoupFor(mesh) is not { } soup) return "rays n/a (mesh not loaded)";
    var m = SpatialOverlap.RefrRotation(r.Rotation);
    var t = new Vector3(r.Position.X, r.Position.Y, r.Position.Z);
    var wv = new Vector3[soup.Vertices.Length];
    for (int i = 0; i < wv.Length; i++) wv[i] = Vector3.Transform(soup.Vertices[i] * r.Scale, m) + t;
    var o = new Vector3(qx, qy, qz);
    var dirs = new (string Name, Vector3 D)[]
    {
        ("+x", Vector3.UnitX), ("-x", -Vector3.UnitX), ("+y", Vector3.UnitY), ("-y", -Vector3.UnitY),
        ("up", Vector3.UnitZ), ("down", -Vector3.UnitZ),
    };
    var hits = dirs.Select(dd => (dd.Name, dd.D, Hit: NearestHitTri(o, dd.D, wv, soup.Indices, 4096f))).ToArray();
    insideFaceKind.TryGetValue(mesh, out var kinds);
    return "rays " + string.Join(" ", hits.Select(h => h.Name + " " + (h.Hit is { } x ? x.T.ToString("0") : "-")))
         + "  facing " + string.Join(" ", hits.Select(h => h.Name + " "
             + (h.Hit is { } x ? Facing(h.D, wv, soup.Indices, x.Tri, kinds) : "-")));
}

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
// The cell's lease, when --reuse-cell is on: created by the worklist loop for each cell, dropped
// when that cell's shots are done, null in every other run. A shot reads it and shares nothing
// beyond it; see CellScene.cs for what may and may not live there.
ViewProbe.CellScene? cellScene = null;
ViewProbe.GraphicsReuseRed? graphicsReuseRed = null;
ViewProbe.GraphicsCell? graphicsCell = null;
var preparedProcessIdentity = new ViewProbe.PreparedProcessIdentity();

int RunCellTextured(string cellTarget, int wlCell, int wlShot,
                    ViewProbe.BatchProgress.ShotPlan? shotPlan,
                    out ViewProbe.BatchProgress.ShotArtifacts shotArtifacts)
{
    var completedArtifacts = new ViewProbe.BatchProgress.ShotArtifacts();
    shotArtifacts = completedArtifacts;
    bool collectArtifacts = shotPlan is not null;
    // Per-cell state is declared OUTSIDE this function, because the flat mode reads it too — so a
    // SECOND call has to clear what the first left. A collection missed here would not throw; it
    // would quietly render cell 2 holding cell 1's references.
    winRef.Clear(); persistentWin.Clear(); refDisabledFlag.Clear(); refEnableParent.Clear(); refEditorIdAll.Clear();
    winWalk.Reset();
    stateArm = null; activeFacts = EnableFactsOf; stateBaselineMemo.Clear();
    stateSpec = cliStateSpec;
    // The unreadable-asset list is per SHOT for the same reason: the census line and the report
    // block describe THIS picture. The process-wide total, which the exit code is spent on, is
    // kept separately and is not cleared here.
    ViewProbe.UnreadableAssets.BeginShot();
    planeHist.Clear(); cellContestedBy.Clear();
    npcRefWhere.Clear(); npcBaseWhere.Clear(); doorWhere.Clear(); refDeletedBy.Clear();
    winNav.Clear(); navByPlugin.Clear();
    navVersionPlugins.Clear(); foreach (var plugin in cliNavVersionPlugins) navVersionPlugins.Add(plugin);
    refCellOf.Clear();
    baseMesh.Clear(); baseIsMarker.Clear(); baseEditorId.Clear(); baseType.Clear(); baseObnd.Clear();
    ltexToTxst.Clear(); ltexEditorId.Clear(); txstDiffuse.Clear(); txstNormal.Clear();
    baseMato.Clear(); matoRec.Clear(); watrRec.Clear(); cellWater.Clear();
    wsWater = null; wsDefaultWaterHeight = float.MaxValue;
    cellWaterTruth.Clear(); wsWaterTruth = null; wsDefaultWaterHeightTruth = float.MaxValue;
    cellAtGrid.Clear(); worldChain.Clear(); stationWorldspaceEd = "";
    stationRefsByGrid.Clear(); stationLandByGrid.Clear(); stationUnreadableLand.Clear(); stationReach = null;
    enableMemo.Clear();
    targetWorldspace = FormKey.Null; targetGrid = null;
    cellKey = null; cellLabel = cellTarget; interior = false;
    visibleSet = null;
    sw.Restart();

    // The shot's route, or the command line's. A LOCAL, like the shot spec below. navMode and
    // cullAbove used to be set from the shot and never cleared, so shot 1's override leaked into
    // shot 2 — an eye-level shot after a sliced top-down lost its ceiling and the beam it existed
    // to show (2026-09-02); both are now reset to the command line's values at the top of every
    // worklist shot, below.
    IReadOnlyList<(float X, float Y)> shotRoute = routeXY;
    string routeLabel = "";
    float? shotFloorZ = null;
    HarnessShared.WorklistShot? shotSpec = null;   // the shot's own props, routes and NPCs, each applied where it belongs below
    // The camera INPUTS this shot renders from, kept so the frame can name the command that
    // reproduces IT rather than the whole worklist's. Null outside a worklist, where the process's
    // own command line already is that command.
    ViewProbe.ReplayCommand.ShotCamera? replayCam = null;

    // A worklist shot IS the camera: eye, aim and fov come from the shot and override whatever the
    // command line asked for, so one file drives both the picture and the bat that stands there.
    if (worklist is not null)
    {
        var wshot = worklist.Cells[wlCell].Shots[wlShot];
        // Measurement only (G5 RED, Gate B8): what the previous shot and the command line left, before the resets.
        graphicsReuseRed?.ShotInputs(wlCell, wlShot, "pre-reset", new Dictionary<string, object?>
        {
            ["navMode"] = navMode, ["cullAbove"] = cullAbove, ["showPlanes"] = showPlanes, ["occlusionMode"] = occlusionMode,
            ["canvasAspect"] = canvasAspect, ["eye"] = eye, ["yaw"] = yaw, ["pitch"] = pitch, ["fov"] = fov,
            ["look"] = look, ["eyeRef"] = eyeRef, ["lookRef"] = lookRef, ["stateSpec"] = stateSpec,
        });
        eye = new System.Numerics.Vector3(wshot.Eye[0], wshot.Eye[1], wshot.Eye[2]);
        var (wsYaw, wsPitch) = HarnessShared.WorklistIo.Aim(wshot);
        yaw = wsYaw; pitch = wsPitch; fov = wshot.Fov;
        look = null; eyeRef = null; lookRef = null;      // the shot is the whole camera, not a base for one
        navMode = cliNavMode; cullAbove = cliCullAbove;     // the run's values first, then this shot's overrides
        showPlanes = cliShowPlanes; occlusionMode = cliOcclusion;
        // The canvas SHAPE is part of the camera, so it resets per shot like the rest. A worklist
        // that mixes a 16:9 shot with a square one must give each the frame it asked for, and a
        // shot with no Aspect INHERITS THE RUN'S -- which is square unless --aspect said otherwise,
        // so a legacy worklist renders exactly as it always did (Piranha, 2026-09-15).
        canvasAspect = cliCanvasAspect;
        if (wshot.Aspect is { } shotAspect) canvasAspect = shotAspect;
        if (wshot.Nav is { } shotNav) navMode = shotNav;
        if (wshot.CullAbove is { } shotCull) cullAbove = shotCull;
        if (wshot.Planes is { } shotPlanes) showPlanes = shotPlanes;
        if (wshot.Occlusion is { } shotOccl) occlusionMode = shotOccl;
        if (wshot.State is { } shotState) stateSpec = shotState;
        if (wshot.Route is { Count: > 1 })
        {
            shotRoute = wshot.Route.Select(w => (w[0], w[1])).ToList();
            routeLabel = wshot.Label;
        }
        shotFloorZ = wshot.FloorZ;      // the MEASURED floor, when the author had one
        shotSpec = wshot;
        replayCam = new ViewProbe.ReplayCommand.ShotCamera(eye.Value, wsYaw, wsPitch, wshot.Fov);
        textured = true;
        Console.WriteLine($"\n=== worklist cell {wlCell + 1}/{worklist.Cells.Count} "
                        + $"shot {wlShot + 1}/{worklist.Cells[wlCell].Shots.Count}: {wshot.Label} ===");
        // Measurement only (G5 RED, Gate B8): the shot's inputs after the per-shot resets and the row's overrides.
        graphicsReuseRed?.ShotInputs(wlCell, wlShot, "post-reset", new Dictionary<string, object?>
        {
            ["navMode"] = navMode, ["cullAbove"] = cullAbove, ["showPlanes"] = showPlanes, ["occlusionMode"] = occlusionMode,
            ["canvasAspect"] = canvasAspect, ["eye"] = eye, ["yaw"] = yaw, ["pitch"] = pitch, ["fov"] = fov,
            ["look"] = look, ["eyeRef"] = eyeRef, ["lookRef"] = lookRef, ["stateSpec"] = stateSpec,
        });
    }

    // The shot's own NPCs, on top of the command line's: the same shape --npc parses to, and watched by
    // the same walk — THIS shot's, which has not started yet. Shot-local, so shot 1's actors do not
    // stand in shot 2's cell.
    var shotNpcParsed = new List<(FormKey Key, System.Numerics.Vector3? Pos, float? HeadingDeg, string? Label, string Raw)>(npcParsed);
    if (shotSpec?.Npcs is { Count: > 0 } wlNpcs)
        foreach (var wn in wlNpcs)
        {
            FormKey nk;
            try { nk = FormKey.Factory(wn.Form.Trim()); }
            catch { Console.Error.WriteLine($"worklist npc '{wn.Form}': not a FormKey (XXXXXX:Plugin.esp)."); return 1; }
            System.Numerics.Vector3? np = wn.Pos is { Count: 3 } wp ? new System.Numerics.Vector3(wp[0], wp[1], wp[2]) : null;
            shotNpcParsed.Add((nk, np, wn.Heading, wn.Label, $"worklist npc {wn.Form}"));
        }
    var shotNpcWatch = new HashSet<FormKey>(shotNpcParsed.Select(sp => sp.Key));

    // The retained walk is keyed by every input the walk consumes. File length/mtime bind each
    // ordered path to the metadata observed at the boundary without paying for a second content read; the worklist
    // selection closes over the filtered NPC/door/nav observations. Camera and presentation flags
    // are deliberately absent because they do not decide which records the walk copies.
    IEnumerable<string> ProcessInputIdentity()
    {
        yield return Path.GetFullPath(Instance); yield return profileName;
        yield return "bos=" + bosMode;
        foreach (var name in new[] { "plugins.txt", "loadorder.txt", "modlist.txt", "archives.txt", "Skyrim.ini", "SkyrimCustom.ini" })
            yield return "profile:" + ViewProbe.PreparedProcessIdentity.FileToken(Path.Combine(profileDir, name));
        if (orderFile is not null)
            yield return "candidate:" + ViewProbe.PreparedProcessIdentity.FileToken(orderFile);
        foreach (var path in order.OrderedPaths)
            yield return "plugin:" + ViewProbe.PreparedProcessIdentity.FileToken(path);
        foreach (var root in assets.LooseRoots.OrderBy(x => x.Dir, StringComparer.OrdinalIgnoreCase))
            yield return $"asset-root:{root.Name}|{Path.GetFullPath(root.Dir)}";
        if (bosMode != "off")
        {
            var currentBosInis = BosDiscovery.Discover(assets.LooseRoots);
            discoveredBosInis ??= currentBosInis;
            foreach (var ini in currentBosInis)
                yield return "bos-rule:" + ViewProbe.PreparedProcessIdentity.FileToken(ini.Path);
        }
    }
    IEnumerable<string> SelectionIdentity()
    {
        yield return cellTarget.Trim();
        yield return $"ring={ringRefs};stations={stationsSpec is not null};terrain={drawTerrain};water={drawWater};stateRecords={stateSpec is not null}";
        if (ringMode != "1") yield return $"ringMode={ringMode};reach={ringReach};cap={ringCap}";
        foreach (var x in shotNpcWatch.Order()) yield return "npc:" + x;
        foreach (var x in doorWatchKeys.Order()) yield return "door:" + x;
        foreach (var x in cliNavVersionPlugins.Order(StringComparer.OrdinalIgnoreCase)) yield return "navsource:" + x;
        foreach (var x in navCellSpecs.Order(StringComparer.Ordinal)) yield return "navcell:" + x;
        foreach (var x in navVersionSpecs.Order(StringComparer.Ordinal)) yield return "navver:" + x;
    }
    string? processKey = cellScene is null ? null : ViewProbe.CellFactsOwnership.Key(ProcessInputIdentity());
    if (cellScene is not null && !preparedProcessIdentity.TryBind(processKey!, out var processRefusal))
    { Console.Error.WriteLine(processRefusal); return 2; }
    string? preparedKey = cellScene is null ? null
        : ViewProbe.CellFactsOwnership.Key(new[] { processKey! }, SelectionIdentity());
    ViewProbe.CellWalkFacts? retainedWalk = null;
    bool retainedWalkHit = cellScene is not null && cellScene.TryFacts(preparedKey!, out retainedWalk);
    if (retainedWalkHit) retainedWalk = retainedWalk!.CopyForShot();
    ViewProbe.CellWalkFacts? builtWalkCore = null;
    bool bosFill = false;

    // --nav-z's band, resolved for THIS shot — `auto` reads the shot's own floor, so it cannot be
    // settled on the command line. Refused rather than defaulted when auto has nothing to read:
    // a storey filter that quietly picked z 0 would draw an empty overlay over a full building.
    float? navZAt = navZ;
    if (navZAuto)
    {
        navZAt = routeZ ?? shotFloorZ;
        if (navZAt is null)
        {
            Console.Error.WriteLine("--nav-z auto has no storey to take: pass --route-z Z, give the worklist shot a floorZ, "
                                  + "or state the height outright with --nav-z Z. The camera's own eye is NOT used — a top-down "
                                  + "sits a thousand units above the floor it is looking at.");
            return 1;
        }
    }

    if (!retainedWalkHit)
    {
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
    __run.Feature("plugins", order.OrderedPaths.Count, "plugins").Stage("find-cell", "walking the order for the target cell", order.OrderedPaths.Count, "plugins");

    int scannedForCell = 0;
    ViewProbe.CellFactsRed.Walk(cellScene, prepass: true);
    foreach (var path in order.OrderedPaths)
    {
        __run.Advance();
        ViewProbe.RenderStages.Tick?.Invoke();
        scannedForCell++;
        // A plugin that cannot be read is refused, not skipped (PluginRead.cs): the target's identity would otherwise
        // come from whichever later plugin happened to be readable.
        ISkyrimModGetter m = ViewProbe.PluginRead.Open(path, scannedForCell - 1, order.OrderedPaths.Count, "the find-cell pass");
        try
        {
        using (m as IDisposable)
        {
            // every cell, a terrain-only one included: a cell is found because it exists, not because something is placed in it
            foreach (var cp in PlacementExtract.FromMod(m, includeEmpty: true))
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
        }
        catch (Exception ex) when (ViewProbe.PluginRead.Wraps(ex))
        { throw ViewProbe.PluginRead.Failed(path, scannedForCell - 1, order.OrderedPaths.Count, "the find-cell pass", ex); }
        if (cellKey is not null) break;
    }
    if (cellKey is null) { Console.Error.WriteLine($"cell '{cellTarget}' not found in the active load order."); return 1; }
    // The walk STOPS at the plugin that has the cell, so the plugins after it were never work.
    // Saying so is the difference between a stage that ended at 1 of 5 and one that ended at 1 of 1.
    ViewProbe.RenderStages.Revise(scannedForCell,
        $"the cell was found in plugin {scannedForCell} of {order.OrderedPaths.Count}; the rest were not walked");
    try { navSelection = new ViewProbe.NavmeshSelection(cellKey.Value, navCellSpecs, navVersionSpecs); }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    try { navLinks = navLinkSpecs.Count > 0 ? new ViewProbe.NavmeshLinks(navLinkSpecs) : null; }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    foreach (var plugin in navSelection.Overrides.Values) navVersionPlugins.Add(plugin);

    // The BOS form index fills on the FIRST walk only (a worklist re-walks per cell; the index does not change).
    bosFill = bosIdx is not null && bosIdx.RecordsSeen == 0;
    // --ring visible|N (VisibleCells.cs): the loaded cells, before the walk whose admission rule reads them.
    if (ringMode != "1" && !interior && targetGrid is { } vtg)
    {
        if (ringMode == "visible")
        {
            if (eye is not { } vEye)
            {
                Console.Error.WriteLine("--ring visible needs the camera before the walk: give --eye X,Y,Z (with --yaw/--pitch or --look X,Y,Z), "
                                      + "not --eye-ref/--look-ref, which are resolved from the walk it would choose the cells for.");
                return 2;
            }
            if (look is null && lookRef is not null)
            { Console.Error.WriteLine("--ring visible cannot use --look-ref (resolved after the walk); give --look X,Y,Z or --yaw/--pitch."); return 2; }
            System.Numerics.Vector3 vLook;
            if (look is { } lk0) vLook = lk0;
            else
            {
                // The camera rule of the render (first-person block below): 0 = +Y, clockwise; positive pitch looks down.
                float yr0 = float.DegreesToRadians(yaw ?? 0f), pr0 = float.DegreesToRadians(pitch);
                vLook = vEye + new System.Numerics.Vector3(MathF.Sin(yr0) * MathF.Cos(pr0), MathF.Cos(yr0) * MathF.Cos(pr0), -MathF.Sin(pr0)) * 512f;
            }
            var eyeCell0 = ((int)MathF.Floor(vEye.X / 4096f), (int)MathF.Floor(vEye.Y / 4096f));
            var bandSw = System.Diagnostics.Stopwatch.StartNew();
            var bands = ViewProbe.VisibleCells.Bands(order.OrderedPaths, targetWorldspace, eyeCell0, ringReach);
            var sel = ViewProbe.VisibleCells.Select(vEye, vLook, fov, canvasAspect, vtg, bands, ringReach, ringCap, bandSw.Elapsed.TotalSeconds);
            visibleSet = sel.Cells;
            Console.WriteLine(ViewProbe.VisibleCells.Describe(sel, ringReach, ringCap, vtg));
        }
        else
        {
            int rr = int.Parse(ringMode, System.Globalization.CultureInfo.InvariantCulture);
            visibleSet = new HashSet<(int X, int Y)>();
            for (int dx = -rr; dx <= rr; dx++)
                for (int dy = -rr; dy <= rr; dy++)
                    visibleSet.Add((vtg.X + dx, vtg.Y + dy));
            Console.WriteLine($"   ring {rr}: {visibleSet.Count} cells loaded ({2 * rr + 1}x{2 * rr + 1} around the target); full references, terrain and water in all of them"
                            + (rr > ViewProbe.WinningRefWalk.EdgeRadius ? $", beyond the game's ±{ViewProbe.WinningRefWalk.EdgeRadius} grid too (the game draws LOD there)" : ""));
        }
    }
    // The ring edge is measured from the target found above (WinningRefs.cs TrackEdge); interiors have none.
    if (!interior && targetGrid is { } edgeGrid) { winWalk.TrackEdge(targetWorldspace, edgeGrid, ringRefs); winWalk.SetVisible(visibleSet); }
    // --lod (DistantLodLayer.cs): the worldspace and the walk's loaded cells, for the LOD drawn beyond them.
    ViewProbe.DistantLodLayer.Target = !interior && targetGrid is not null ? targetWorldspace : FormKey.Null;
    ViewProbe.DistantLodLayer.WalkCells = visibleSet;
    ViewProbe.RenderStages.Open(ViewProbe.RenderStages.RecordWalk,
        "the winning state: every plugin, for the refs and navmeshes that win",
        order.OrderedPaths.Count, "plugins");
    ViewProbe.CellFactsRed.Walk(cellScene, prepass: false);
    // The --stations floor (WalkSideTables.cs): a last-sighting filing over every cell of every plugin.
    var stationFloor = ViewProbe.StationFloorFiling.Begin(stationsSpec is not null, targetWorldspace, targetGrid, StationFloorRadius);
    int walkIndex = -1;
    foreach (var path in order.OrderedPaths)
    {
        ViewProbe.RenderStages.Advance();
        walkIndex++;
        // Refused, not skipped (PluginRead.cs): a plugin this walk cannot read would leave an earlier plugin's version
        // of every record it overrides in the winning state.
        ISkyrimModGetter m = ViewProbe.PluginRead.Open(path, walkIndex, order.OrderedPaths.Count, "the winning-state walk");
        try
        {
        using (m as IDisposable)
        {
            // The target worldspace's large-reference list in this plugin, before its cells (WinningRefs.cs).
            winWalk.ObserveLarge(m);
            stationFloor?.ObserveLists(m);   // and the station floor's (FloorFeed reach), from the same records
            if (bosFill) bosLight.Add((m.ModKey, m.IsSmallMaster || m.ModKey.Type == ModType.Light));
            foreach (var rec in m.EnumerateMajorRecords())
            {
                ViewProbe.RenderStages.Tick?.Invoke();
                if (bosFill) bosIdx!.Note(rec);
                ViewProbe.CellLighting.Note(rec);   // --cell-lighting: LIGH, LGTM, IMGS and interior XCLL (CellLighting.cs)
                stationFloor?.NoteBase(rec);   // the station floor reach's base boxes (FloorFeed)
                // Landscape texture chain: LTEX -> TXST -> diffuse path, winner-takes-all in load
                // order, so terrain quadrants can be drawn with their real winning textures.
                if (rec is ILandscapeTextureGetter lt)
                {
                    ViewProbe.GrassLayer.NoteLtex(lt);   // its grasses (LTEX GNAM), for the grass layer
                    if (lt.TextureSet.FormKeyNullable is { } ts) ltexToTxst[lt.FormKey] = ts;
                    if (!string.IsNullOrWhiteSpace(lt.EditorID)) ltexEditorId[lt.FormKey] = lt.EditorID!;
                    continue;
                }
                if (rec is ITextureSetGetter txst)
                {
                    if (!string.IsNullOrWhiteSpace(txst.Diffuse?.GivenPath)) txstDiffuse[txst.FormKey] = txst.Diffuse!.GivenPath;
                    if (!string.IsNullOrWhiteSpace(txst.NormalOrGloss?.GivenPath)) txstNormal[txst.FormKey] = txst.NormalOrGloss!.GivenPath;
                    continue;
                }
                // WATR — the water body's own colours and wave-noise layers. Values are COPIED
                // out, never held: the overlay is disposed at the end of this using block, and a
                // retained getter would read freed memory.
                // --weather (SceneWeather.cs): the named WTHR, winner-takes-all in load order.
                if (rec is IImageSpaceGetter imgs) ViewProbe.SceneWeather.NoteImageSpace(imgs);
                // The sun's path (--weather): the target worldspace's climate and that climate's timing.
                if (rec is IClimateGetter clmt) ViewProbe.SceneWeather.NoteClimate(clmt);
                // The exterior's default weather comes from the cell's region (SceneWeather.Resolve).
                if (rec is IRegionGetter regn) ViewProbe.SceneWeather.NoteRegion(regn);
                if (rec is ICellGetter wcell && !wcell.Flags.HasFlag(Cell.Flag.IsInteriorCell)) ViewProbe.SceneWeather.NoteCell(wcell);
                if (rec is IWorldspaceGetter wrld && wrld.FormKey == targetWorldspace) ViewProbe.SceneWeather.WorldClimate = wrld.Climate.FormKeyNullable;
                if (rec is IWorldspaceGetter wrldAny) ViewProbe.DistantLodLayer.Note(wrldAny);
                if (rec is IGrassGetter gras) ViewProbe.GrassLayer.NoteGrass(gras);   // GRAS: the grass layer's types (GrassLayer.cs)   // --lod: which LOD files (DistantLodLayer.cs)
                // Every WTHR is kept; SceneWeather.Resolve picks one once the target is known.
                if (rec is IWeatherGetter wthr)
                {
                    ViewProbe.SceneWeather.NoteWeather(wthr);
                    continue;
                }
                if (rec is IWaterGetter wtr)
                {
                    watrRec[wtr.FormKey] = (wtr.EditorID ?? wtr.FormKey.ToString(),
                                            wtr.NoiseLayerOneTexture?.GivenPath,
                                            wtr.NoiseLayerOneUvScale,
                                            Rgb(wtr.ShallowColor), Rgb(wtr.DeepColor),
                                            Rgb(wtr.ReflectionColor), wtr.WaterReflectivity,
                                            wtr.Opacity, wtr.WaterFresnel,
                                            wtr.FogAboveWaterDistanceNearPlane, wtr.FogAboveWaterDistanceFarPlane,
                                            wtr.FogAboveWaterAmount, wtr.NoiseFalloff,
                                            new System.Numerics.Vector3(wtr.SpecularSunPower, wtr.SpecularSunSpecularMagnitude,
                                                                        wtr.DepthSpecularLighting));
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
                                             mato.Model?.File.GivenPath, mato.NoiseUvScale);
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
                    baseType[smr.FormKey] = rec.GetType().Name.Replace("BinaryOverlay", "");
                    // OBND as the record states it — its own offset kept, not centred on the
                    // origin — so the world box lands where the engine's bounds do.
                    if (rec is IObjectBoundedGetter ob && ob.ObjectBounds is { } bnd)
                        baseObnd[smr.FormKey] = (
                            new Vec3(Math.Min(bnd.First.X, bnd.Second.X), Math.Min(bnd.First.Y, bnd.Second.Y), Math.Min(bnd.First.Z, bnd.Second.Z)),
                            new Vec3(Math.Max(bnd.First.X, bnd.Second.X), Math.Max(bnd.First.Y, bnd.Second.Y), Math.Max(bnd.First.Z, bnd.Second.Z)));
                    else baseObnd.Remove(smr.FormKey);
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

            string plgName = m.ModKey.FileName.String;
            // LastSighting.Cells: a cell whose only content is a deletion must still reach the winning-ref walk (K6), and
            // one that carries only its LAND must reach the station floor. Every other table below sees exactly the cells
            // it saw before (a cell with neither references, navmeshes nor deletions goes to the floor alone).
            foreach (var cp in HarnessShared.LastSighting.Cells(m))
            {
                ViewProbe.RenderStages.Tick?.Invoke();
                // The --stations floor neighbourhood (WalkSideTables.cs).
                stationFloor?.Observe(cp, plgName);
                // The navmesh selection sees every cell: a later sighting anywhere, a deletion-only copy included, can take
                // a navmesh out of the winners (NavmeshSelection.Observe).
                navSelection.Observe(cp, plgName, navVersionPlugins, winNav, navByPlugin);
                navLinks?.Observe(cp, plgName);
                if (cp.Refs.Count == 0 && cp.Navmeshes.Count == 0 && cp.DeletedRefs is not { Count: > 0 }) continue;
                if (bosFill) bosIdx!.NoteCell(cp.Cell, cp.Worldspace);
                foreach (var r in cp.Refs)
                {
                    if (bosFill) bosIdx!.NotePlaced(r.FormKey, r.Persistent);
                    if (r.InitiallyDisabled) refDisabledFlag.Add(r.FormKey); else refDisabledFlag.Remove(r.FormKey);
                    if (stateSpec is not null && !string.IsNullOrWhiteSpace(r.EditorId)) refEditorIdAll[r.FormKey] = r.EditorId!;
                    if (r.EnableParent is { } epk) refEnableParent[r.FormKey] = (epk, r.EnableParentOpposite);
                    else refEnableParent.Remove(r.FormKey);
                    if (r.Base is { } pbk && pbk == planeMarkerKey) ViewProbe.PlaneHistory.Record(planeHist, r, plgName);
                }
                // Which refs the winning state admits, and where: WinningRefs.cs (persistent refs are
                // filed for the by-position merge; target and ring cells admit theirs).
                var (isTarget, inRing) = ViewProbe.WinningRefWalk.Classify(cp, cellKey, interior, ringRefs, targetWorldspace, targetGrid, visibleSet);
                winWalk.Observe(cp, isTarget, inRing);
                // The "where is it" tables: deletions, watched NPCs by reference and by base, watched doors (WalkSideTables.cs).
                ViewProbe.RefWhere.Observe(cp, plgName, whereTables, shotNpcWatch, doorWatchKeys, m.ModKey);
                if (isTarget)
                {
                    cellLabel = cp.CellEditorId ?? cp.Cell.ToString();
                    if (!ViewProbe.PlaneHistory.IsBethesda(plgName) && !cellContestedBy.Contains(plgName)) cellContestedBy.Add(plgName);
                }
            }
        }
        }
        catch (Exception ex) when (ViewProbe.PluginRead.Wraps(ex))
        { throw ViewProbe.PluginRead.Failed(path, walkIndex, order.OrderedPaths.Count, "the winning-state walk", ex); }
    }
    ViewProbe.StationFloorFiling.Finish(stationFloor, targetWorldspace, stationRefsByGrid, stationLandByGrid, stationUnreadableLand);
    stationReach = stationFloor?.ReachOf(targetWorldspace);
    // HOUSECARL_WINSTATE_DUMP: the render's selected navmeshes after the walk, for an independent reader.
    if (HarnessShared.WinStateDump.Asked)
        HarnessShared.WinStateDump.Write("viewprobe-navsel", new
        {
            cells = navSelection.Cells.Keys.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal).ToList(),
            winNav = winNav.Values.OrderBy(n => n.FormKey.ToString(), StringComparer.Ordinal)
                          .Select(n => HarnessShared.WinStateDump.Nav(n, navSelection.Provenance[n].Cell, navSelection.Provenance[n].Plugin)).ToList(),
        });
    try
    {
        navSelection.Validate(ringRefs, winNav);
        _ = navSelection.Substitute(winNav, navByPlugin); // refuse unresolved substitutions before building assets
    }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    }
    else
    {
        var f = retainedWalk!;
        cellKey = f.CellKey; cellLabel = f.CellLabel; interior = f.Interior;
        targetWorldspace = f.TargetWorldspace; targetGrid = f.TargetGrid;
        foreach (var x in f.WinningRefs) winRef[x.Key] = x.Value;
        foreach (var x in f.PersistentRefs) persistentWin[x.Key] = x.Value;
        winWalk.Reset(f.TakenOut);
        if (!f.Interior && f.TargetGrid is { } edgeGridR) { winWalk.TrackEdge(f.TargetWorldspace, edgeGridR, ringRefs); winWalk.RestoreEdge(f.EdgeRefs); if (f.LargeState is { } ls) winWalk.RestoreLarge(ls); }
        foreach (var x in f.DisabledFlags) refDisabledFlag.Add(x);
        foreach (var x in f.EnableParents) refEnableParent[x.Key] = x.Value;
        foreach (var x in f.RefEditorIds) refEditorIdAll[x.Key] = x.Value;
        foreach (var x in f.RefCells) refCellOf[x.Key] = x.Value;
        foreach (var x in f.PlaneHistories) planeHist[x.Key] = x.Value;
        cellContestedBy.AddRange(f.CellContestedBy);
        foreach (var x in f.NpcRefs) npcRefWhere[x.Key] = x.Value;
        foreach (var x in f.NpcBases) npcBaseWhere[x.Key] = x.Value;
        foreach (var x in f.Doors) doorWhere[x.Key] = x.Value;
        foreach (var x in f.DeletedBy) refDeletedBy[x.Key] = x.Value;
        navSelection = f.Navigation.Selection;
        foreach (var x in f.Navigation.Winners) winNav[x.Key] = x.Value;
        foreach (var x in f.Navigation.Versions) navByPlugin[x.Key] = x.Value;
        foreach (var x in navSelection.Overrides.Values) navVersionPlugins.Add(x);
        foreach (var x in f.BaseMeshes) baseMesh[x.Key] = x.Value;
        foreach (var x in f.MarkerBases) baseIsMarker.Add(x);
        foreach (var x in f.BaseEditorIds) baseEditorId[x.Key] = x.Value;
        foreach (var x in f.BaseTypes) baseType[x.Key] = x.Value;
        foreach (var x in f.BaseBounds) baseObnd[x.Key] = x.Value;
        foreach (var x in f.LtexToTxst) ltexToTxst[x.Key] = x.Value;
        foreach (var x in f.LtexEditorIds) ltexEditorId[x.Key] = x.Value;
        foreach (var x in f.TxstDiffuse) txstDiffuse[x.Key] = x.Value;
        foreach (var x in f.TxstNormal) txstNormal[x.Key] = x.Value;
        foreach (var x in f.BaseMato) baseMato[x.Key] = x.Value;
        foreach (var x in f.Mato) matoRec[x.Key] = x.Value;
        foreach (var x in f.WaterRecords) watrRec[x.Key] = x.Value;
        foreach (var x in f.CellWater) cellWater[x.Key] = x.Value;
        wsWater = f.WorldWater; wsDefaultWaterHeight = f.WorldDefaultWaterHeight;
        foreach (var x in f.CellWaterTruth) cellWaterTruth[x.Key] = x.Value;
        wsWaterTruth = f.WorldWaterTruth; wsDefaultWaterHeightTruth = f.WorldDefaultWaterHeightTruth;
        foreach (var x in f.CellsAtGrid) cellAtGrid[x.Key] = x.Value;
        foreach (var x in f.WorldChain) worldChain[x.Key] = x.Value;
        stationWorldspaceEd = f.StationWorldspaceEditorId;
        foreach (var x in f.StationRefs) stationRefsByGrid[x.Key] = x.Value;
        foreach (var x in f.StationLand) stationLandByGrid[x.Key] = x.Value;
        foreach (var x in f.StationUnreadableLand) stationUnreadableLand[x.Key] = x.Value;
        stationReach = f.StationReach;
    }
    // --nav-link: from this walk's own last sightings, in both render modes (a reused walk refuses in its row)
    navLinks?.Print(reportedConfiguration?.Identity("load-time", stateSpec ?? "baseline").Fingerprint);
    if (cellScene is not null && !retainedWalkHit)
    {
        // Capture the raw walk boundary before BOS, state arms, persistent merging, synthetic
        // props or any other per-shot projection mutates the working dictionaries.
        var navGraph = navSelection!.CopyGraph(winNav, navByPlugin);
        builtWalkCore = new ViewProbe.CellWalkFacts
        {
            CellKey = cellKey!.Value, CellLabel = cellLabel, Interior = interior,
            TargetWorldspace = targetWorldspace, TargetGrid = targetGrid,
            WinningRefs = winRef.ToDictionary(x => x.Key, x => ViewProbe.CellWalkFacts.Copy(x.Value)),
            TakenOut = winWalk.TakenOutSnapshot(),
            EdgeRefs = winWalk.EdgeSnapshot().ToDictionary(x => x.Key, x => (ViewProbe.CellWalkFacts.Copy(x.Value.Ref), x.Value.Cell)),
            LargeState = winWalk.LargeSnapshot(),
            DeletedBy = new(refDeletedBy),
            PersistentRefs = persistentWin.ToDictionary(x => x.Key,
                x => (x.Value.Ws, ViewProbe.CellWalkFacts.Copy(x.Value.Ref))),
            DisabledFlags = refDisabledFlag.ToHashSet(), EnableParents = new(refEnableParent),
            RefEditorIds = new(refEditorIdAll), RefCells = new(refCellOf),
            PlaneHistories = planeHist.ToDictionary(x => x.Key, x => x.Value.Copy()),
            CellContestedBy = cellContestedBy.ToArray(),
            NpcRefs = npcRefWhere.ToDictionary(x => x.Key,
                x => (x.Value.Cell, x.Value.CellEd, ViewProbe.CellWalkFacts.Copy(x.Value.Ref))),
            NpcBases = npcBaseWhere.ToDictionary(x => x.Key, x => x.Value.ToDictionary(y => y.Key,
                y => (y.Value.Cell, y.Value.CellEd, ViewProbe.CellWalkFacts.Copy(y.Value.Ref)))),
            Doors = doorWhere.ToDictionary(x => x.Key,
                x => (x.Value.Cell, x.Value.CellEd, ViewProbe.CellWalkFacts.Copy(x.Value.Ref))),
            Navigation = navGraph,
            BaseMeshes = new(baseMesh), MarkerBases = baseIsMarker.ToHashSet(),
            BaseEditorIds = new(baseEditorId), BaseTypes = new(baseType), BaseBounds = new(baseObnd),
            LtexToTxst = new(ltexToTxst), LtexEditorIds = new(ltexEditorId), TxstDiffuse = new(txstDiffuse), TxstNormal = new(txstNormal),
            BaseMato = new(baseMato), Mato = new(matoRec), WaterRecords = new(watrRec),
            CellWater = new(), WorldWater = null, WorldDefaultWaterHeight = float.MaxValue,
            CellWaterTruth = new(), WorldWaterTruth = null, WorldDefaultWaterHeightTruth = float.MaxValue,
            CellsAtGrid = new(), WorldChain = new(StringComparer.OrdinalIgnoreCase),
            StationWorldspaceEditorId = "",
            StationRefs = stationRefsByGrid.ToDictionary(x => x.Key,
                x => x.Value.ToDictionary(y => y.Key, y => ViewProbe.CellWalkFacts.Copy(y.Value))),
            StationLand = stationLandByGrid.ToDictionary(x => x.Key, x => (float[,])x.Value.Clone()),
            StationUnreadableLand = new(stationUnreadableLand),
            StationReach = stationReach,
            TerrainPatches = Array.Empty<ViewProbe.TerrainPatchFact>(),
            TerrainLayers = Array.Empty<ViewProbe.TerrainLayerFact>(),
            LandHeights = Array.Empty<ViewProbe.LandHeightFact>(),
        };
    }
    // THE RING EDGE (WinningRefs.cs): admitted here, after the walk and its retained capture and before anything that
    // reads the winning state - the BOS overlay, the state arm, the persistent merge, planes, drawing and every count -
    // so an admitted reference takes exactly the path a ring reference takes. Its box in its own frame: the base's OBND
    // for a drawable base (a model, not an is-marker base); an occlusion plane's XPRM primitive (full extents, centred).
    edgeAdmittedCount = edgePlanesAdmittedCount = 0;
    largeAdmittedCount = largePlanesAdmittedCount = largeListedCount = 0; largeIsChild = false;
    if (!interior && targetGrid is not null)
    {
        (Vec3 Min, Vec3 Max)? EdgeLocalBounds(PlacedRef r)
        {
            if (r.Base is not { } bk) return null;
            if (bk == planeMarkerKey)
                return r.Primitive is { } pr
                    ? (new Vec3(-pr.Bounds.X / 2f, -pr.Bounds.Y / 2f, -pr.Bounds.Z / 2f), new Vec3(pr.Bounds.X / 2f, pr.Bounds.Y / 2f, pr.Bounds.Z / 2f))
                    : null;
            if (!baseMesh.ContainsKey(bk) || baseIsMarker.Contains(bk)) return null;
            return baseObnd.TryGetValue(bk, out var ob) ? ob : null;
        }
        edgeAdmittedCount = winWalk.MergeEdge(EdgeLocalBounds);   // ring-edge: admit
        edgePlanesAdmittedCount = winWalk.EdgeAdmittedKeys.Count(k => winRef[k].Base is { } eb && eb == planeMarkerKey);
        // Large references: the same box test, three to five cells out, for references the selected list names.
        largeAdmittedCount = winWalk.MergeLarge(EdgeLocalBounds, largeChildUnion);
        largePlanesAdmittedCount = winWalk.LargeAdmittedKeys.Count(k => winRef[k].Base is { } lb && lb == planeMarkerKey);
        largeListedCount = winWalk.LargeListed(largeChildUnion); largeIsChild = winWalk.LargeIsChild;
    }

    if (bosIdx is not null && bosLayer is null)
    {
        if (bosFill) bosIdx.SetLoadOrder(bosLight);
        var bosInis = discoveredBosInis ??= BosDiscovery.Discover(assets.LooseRoots);
        bosLayer = BaseObjectSwaps.Load(bosInis, bosIdx);
        Console.WriteLine($"   BOS {bosMode}: {bosInis.Count} *_SWAP.ini read from the VFS ({bosLayer.LinesRead:N0} lines, {bosLayer.LinesFailed:N0} the game refuses, "
                        + $"{bosLayer.Anomalies.Count:N0} anomalies — bosprobe lists them); every drawn ref takes the base and transform the game loads");
    }
    // The overlay runs on EVERY call, not only the one that loads the layer: bosLayer is loaded once per PROCESS
    // (the guard above), while refDisabledFlag is cleared and rebuilt from the records at the top of every call — so
    // folding it inside the load block left a worklist's second shot resolving its chain on the records' own flags
    // alone, with the counter below still reporting the re-flags the chain never saw.
    if (bosIdx is not null && bosLayer is not null)
    {
        // Q33 (measured in game 2026-09-06): a flags(0x800)/flagsC(0x800) override replaces the reference's OWN
        // initially-disabled flag — the enable chain still decides presence. So it goes into the flag map the chain
        // reads, BEFORE anything resolves, rather than being applied as a filter after it. Two things that fixes: a
        // reference whose enable parent is ON stays drawn even where an override sets the flag (the engine keeps it),
        // and a clear on a parentless record-disabled reference now DRAWS it, which the post-chain filter could never
        // do — it had already dropped the reference before BOS was consulted.
        int bosOwnFlagChanges = 0;
        foreach (var (fk, r) in winRef)
        {
            if (!refCellOf.TryGetValue(fk, out var rc)) continue;
            var res0 = BaseObjectSwaps.Resolve(bosLayer, bosIdx, fk, r.RecordType, r.Base ?? default, rc, r.EncounterZone);
            if (res0.Properties is not { IsValid: true }) continue;
            var (_, _, _, dis0) = res0.Apply(r.Position, r.Rotation, r.Scale, r.InitiallyDisabled);
            if (dis0 == r.InitiallyDisabled) continue;
            if (dis0) refDisabledFlag.Add(fk); else refDisabledFlag.Remove(fk);
            bosOwnFlagChanges++;
        }
        if (bosOwnFlagChanges > 0)
            Console.WriteLine($"   BOS own-flag overrides: {bosOwnFlagChanges:N0} reference(s) re-flagged into the enable chain "
                            + "(a reference with an enable parent keeps the parent's answer — measured, Q33)");
    }

    // --state: the arm over the whole order's chains (a marker in the persistent cell governs refs in the grid cells). A
    // switch is any winning XESP parent that is a placed reference; a token that names none is refused, never ignored.
    if (stateSpec is not null)
    {
        var parents = new HashSet<FormKey>(refEnableParent.Values.Select(v => v.Parent));
        var byEdid = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, e) in refEditorIdAll) if (parents.Contains(k)) byEdid.TryAdd(e, k);
        FormKey? Tok(string t)
        {
            if (byEdid.TryGetValue(t, out var k)) return k;
            try { var fk = FormKey.Factory(t); return parents.Contains(fk) ? fk : null; } catch { return null; }
        }
        var flips = new List<FormKey>();
        if (!stateSpec.Equals("baseline", StringComparison.OrdinalIgnoreCase))
        {
            IEnumerable<string> tokens;
            if (stateSpec.StartsWith("stage:", StringComparison.OrdinalIgnoreCase))
            {
                if (switchMapPath is null) { Console.Error.WriteLine($"--state {stateSpec} needs --switch-map <switchprobe --json file>"); return 1; }
                tokens = HarnessShared.SwitchMap.Read(switchMapPath).StageFlips(stateSpec[6..]) ?? Array.Empty<string>();
                if (!tokens.Any()) { Console.Error.WriteLine($"--state {stateSpec}: the switch map names no such quest stage"); return 1; }
            }
            else tokens = stateSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var t in tokens)
            {
                if (Tok(t) is not { } k) { Console.Error.WriteLine($"--state: '{t}' is not a switch in this order (no winning reference names it as an enable parent)"); return 1; }
                if (!flips.Contains(k)) flips.Add(k);
            }
        }
        stateArm = new HarnessShared.StateArm(stateSpec, flips);
        activeFacts = stateArm.Wrap(EnableFactsOf, RefEnabledBaselineResolution);
        enableMemo.Clear();
        int arrive = 0, depart = 0;
        foreach (var k in winRef.Keys) { bool was = RefEnabledBaseline(k), now = RefEnabled(k); if (now && !was) arrive++; else if (!now && was) depart++; }
        Console.WriteLine($"   STATE {stateArm.Name} ({stateView}): {flips.Count} switch(es) flipped — in the loaded cells {arrive:N0} ref(s) arrive, {depart:N0} depart; every drawn ref's existence is its runtime enable state under the flip");
    }
    EnableResolution RefEnabledBaselineResolution(FormKey fk) => EnableState.Resolve(fk, EnableFactsOf, stateBaselineMemo);
    string StateRootLabel(FormKey fk)
    {
        if (stateArm is null) return "";
        var seen = new HashSet<FormKey>();
        var k = fk;
        while (seen.Add(k))
        {
            if (stateArm.Flips.Contains(k)) return refEditorIdAll.TryGetValue(k, out var e) ? e : k.ToString();
            if (!refEnableParent.TryGetValue(k, out var p)) break;
            k = p.Parent;
        }
        return "(the flip)";
    }

    Console.WriteLine($"cell {cellLabel} {(interior ? "(interior)" : "(exterior)")}  {cellKey}");
    // the exterior cell's grid, on its own line: shotplan centres the drawn cells it reads a clear bearing against on it
    if (!interior && targetGrid is { } tgg) Console.WriteLine($"   target grid {tgg.X},{tgg.Y}");
    if (!interior && targetGrid is { } tgp)
    {
        // Persistent refs merge by position over the whole REF-LOADED area (WinningRefs.cs).
        int ringR = ringRefs ? 1 : 0;
        int addedPersist = winWalk.MergePersistent(targetWorldspace, tgp, ringRefs);
        if (addedPersist > 0)
            Console.WriteLine($"   +{addedPersist} persistent worldspace ref(s) inside the rendered {(visibleSet is not null ? $"{visibleSet.Count} cells'" : ringR > 0 ? "3x3 cells'" : "cell's")} bounds (doors live here)");
        if (edgeAdmittedCount > 0)
            Console.WriteLine($"   +{edgeAdmittedCount} reference(s) anchored {(visibleSet is not null ? "one cell outside the loaded cells whose bounds enter them" : "two cells out whose bounds enter the loaded 3x3")} ({edgePlanesAdmittedCount} occlusion plane(s); "
                            + $"game grid ±{ViewProbe.WinningRefWalk.EdgeRadius}, inferred: uGridsToLoad is unset in the INIs)");
        if (largeAdmittedCount > 0)
            Console.WriteLine($"   +{largeAdmittedCount} large reference(s) anchored {(visibleSet is not null ? $"up to {ViewProbe.WinningRefWalk.LargeRadius} cells out, beyond the loaded cells' border, whose bounds enter them" : $"3-{ViewProbe.WinningRefWalk.LargeRadius} cells out whose bounds enter the loaded 3x3")} "
                            + $"({largePlanesAdmittedCount} occlusion primitive(s), by record; list: {(largeIsChild ? (largeChildUnion ? "child worldspace, its own lists' union" : "child worldspace, none") : "union of every plugin's list")}, "
                            + $"{largeListedCount:N0} listed)");
    }
    // A reference an earlier plugin placed in the loaded cells and a later plugin filed elsewhere is not drawn (the
    // invariant in WinningRefs.cs). Said only when it happened, so a console with no such move reads as before.
    if (winWalk.TakenOutCount is > 0 and var takenOut)
        Console.WriteLine($"   -{takenOut} ref(s) an earlier plugin placed in the loaded cells and a later plugin moved elsewhere: not drawn (the later plugin wins)");
    Console.WriteLine($"winning state: {winRef.Count:N0} refs, {winNav.Count:N0} navmeshes  ({sw.Elapsed.TotalSeconds:0.0}s)");
    if (stationsSpec is not null) ViewProbe.StationFloorFiling.Dump(targetWorldspace, targetGrid, StationFloorRadius, stationRefsByGrid, stationLandByGrid, stationUnreadableLand);
    if (winRefsDump is not null && (!dumpTerrain || interior))
    {
        ViewProbe.WinningRefsDump.Write(winRefsDump, cellTarget, cellKey!.Value, interior, targetWorldspace, targetGrid, ringRefs,
            order.OrderedPaths, winRef, refCellOf, persistentWin, winWalk.TakenOutKeys(), null, null);
        return 0;
    }

    // The shot's synthetic props: what a runtime PlaceAtMe stood in the cell during a test, put back
    // into the winning state as invented refs so the picture shows the obstacle the numbers were
    // measured against. Invented and said so — a high ID in the base's own plugin, never written; the
    // base itself is real and must carry a model, or there is nothing to draw and the shot is refused.
    var constructedRefs = new HashSet<FormKey>();
    if (shotSpec?.Props is { Count: > 0 } wlProps)
    {
        int pi = 0;
        foreach (var wp in wlProps)
        {
            FormKey bk;
            try { bk = FormKey.Factory(wp.Form.Trim()); }
            catch { Console.Error.WriteLine($"worklist prop '{wp.Form}': not a FormKey (XXXXXX:Plugin.esp)."); return 1; }
            if (!baseMesh.TryGetValue(bk, out var pmesh))
            {
                Console.Error.WriteLine($"worklist prop '{wp.Form}'{(wp.Label is null ? "" : $" ({wp.Label})")}: no record with a model by that key in the load order"
                                      + " — a prop names a BASE (STAT, MSTT, …), not a placed ref.");
                return 1;
            }
            FormKey fk;
            do { fk = new FormKey(bk.ModKey, (uint)(0xFFF000 + pi++)); } while (winRef.ContainsKey(fk));
            constructedRefs.Add(fk);
            winRef[fk] = new PlacedRef(fk, "PlacedObject", wp.Label, new Vec3(wp.Pos[0], wp.Pos[1], wp.Pos[2]),
                                       new Vec3(0f, 0f, float.DegreesToRadians(wp.Yaw)), wp.Scale, bk,
                                       Persistent: false, InitiallyDisabled: false, Deleted: false);
            Console.WriteLine($"   +prop {fk}{(wp.Label is null ? "" : $" \"{wp.Label}\"")} = {bk}"
                            + (baseEditorId.TryGetValue(bk, out var pEd) ? $" {pEd}" : "") + $"  {pmesh}"
                            + $"  at {wp.Pos[0]:0},{wp.Pos[1]:0},{wp.Pos[2]:0} yaw {wp.Yaw:0}° scale {wp.Scale:0.##}"
                            + "  (synthetic: from the worklist shot, in no plugin)");
        }
    }

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
    if (!textured && (highlightSpecs.Count > 1 || highlightSpecs.Any(s => s.Contains('='))))
    {
        Console.Error.WriteLine("flat diagnostic mode accepts one bare --ref FORMKEY only; repeatable or coloured --ref values require --tex or --report-mode");
        return 1;
    }

    // A non-square canvas that silently did nothing would be the same class of fault it exists to
    // fix: a caller believing they captured the frame they asked for. Refuse where it cannot apply.
    if (MathF.Abs(canvasAspect - 1f) > 1e-6f)
    {
        if (!textured)
        { Console.Error.WriteLine("--aspect shapes the first-person CAPTURE canvas; the flat diagnostic mode's orbit views are square. Add --tex/--eye or drop --aspect."); return 2; }
        if (interactiveMode)
        { Console.Error.WriteLine("--aspect shapes the offscreen capture canvas; --interactive opens a resizable window whose shape is the window's. Drop one of the two."); return 2; }
    }
    // --size is the WIDTH; the height follows the aspect. Rounded to an even number of pixels so a
    // centre column/row exists and MSAA resolve has no half-pixel to argue about.
    // The 1:1 case takes --size verbatim rather than going through the arithmetic: an odd --size
    // would round to even and quietly change a square frame that nobody asked to change.
    int canvasH = MathF.Abs(canvasAspect - 1f) <= 1e-6f ? size
                                                        : (int)MathF.Round(size / canvasAspect / 2f) * 2;
    if (canvasH < 2)
    { Console.Error.WriteLine($"--aspect {canvasAspect:0.####} with --size {size} gives a {canvasH}px height; pick a larger --size or a smaller aspect."); return 2; }

    if (textured)
    {
        var highlightColors = new Dictionary<FormKey, System.Numerics.Vector3>();
        var namedHighlightColors = new Dictionary<string, System.Numerics.Vector3>(StringComparer.OrdinalIgnoreCase)
        { ["orange"] = new(1f,.35f,.10f), ["cyan"] = new(.05f,.85f,.95f), ["magenta"] = new(.90f,.20f,.95f),
          ["yellow"] = new(1f,.88f,.10f), ["green"] = new(.05f,.85f,.30f), ["blue"] = new(.25f,.45f,1f) };
        foreach (var hs in highlightSpecs)
        {
            var eq = hs.LastIndexOf('='); var keyText = eq < 0 ? hs : hs[..eq]; var colorText = eq < 0 ? "orange" : hs[(eq + 1)..];
            if (!namedHighlightColors.TryGetValue(colorText, out var hc))
            { Console.Error.WriteLine($"--ref '{hs}': colour must be orange, cyan, magenta, yellow, green or blue."); return 1; }
            try { highlightColors[FormKey.Factory(keyText)] = hc; }
            catch { Console.Error.WriteLine($"--ref '{hs}' is not FORMKEY[=COLOR]."); return 1; }
        }

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

        // Measurement only (G5 RED, Gate B8): the camera this shot resolved.
        graphicsReuseRed?.ShotCamera(new Dictionary<string, object?>
        {
            ["eyePos"] = eyePos, ["lookPos"] = lookPos, ["fov"] = fov, ["canvasAspect"] = canvasAspect,
        });
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
            int badConflictKeys = 0;
            foreach (var c in cf.Conflicts)
                foreach (var rec in c.Necessary.Concat(c.Alternative ?? Array.Empty<HarnessShared.ConflictRecord>()))
                {
                    FormKey fk;
                    // A FormKey that does not parse is counted and named, not skipped in silence (a bad input file).
                    try { fk = FormKey.Factory(rec.FormKey); }
                    catch { Console.Error.WriteLine($"   conflict file: '{rec.FormKey}' is not a FormKey; that record is not highlighted."); badConflictKeys++; continue; }
                    if (rec.Role == "navmesh" && winNav.ContainsKey(fk)) MarkRed(fk, null);
                    else if (rec.Role == "door" && winRef.ContainsKey(fk)) conflictDoors.Add(fk);
                }
            if (badConflictKeys > 0)
                Console.Error.WriteLine($"   conflict file: {badConflictKeys} record(s) with an unparseable FormKey were not highlighted.");
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
                    Console.WriteLine(refDeletedBy.TryGetValue(dfk, out var delBy)
                        ? $"   implicated door {dfk} was deleted by {delBy} (no placement data): the game does not load it"
                        : doorWhere.TryGetValue(dfk, out var dw)
                        ? $"   implicated door {dfk} is NOT in the rendered cell(s): it sits in {dw.CellEd ?? dw.Cell.ToString()} at {dw.Ref.Position.X:0},{dw.Ref.Position.Y:0},{dw.Ref.Position.Z:0}"
                        : $"   implicated door {dfk}: no placed ref with this FormKey anywhere in the load order");
            if (redHere == 0 && conflictDoors.Count == 0)
                Console.WriteLine($"   (no conflict in that file names a navmesh or door in THIS cell — nothing to paint)");
        }

        var scene = new List<ViewProbe.SceneRef>();
        ViewProbe.CellLighting.Placed.Clear();
        ViewProbe.CellLighting.TargetCell = interior ? cellKey : null;
        ViewProbe.SceneWeather.Resolve(interior, cellKey);
        int fxSkipped = 0, markerSkipped = 0;
        int deletedSkipped = 0, disabledFlagSkipped = 0, disabledParentSkipped = 0, enabledByParentDrawn = 0, noModel = 0;
        int bosSwapped = 0, bosRandomCount = 0, bosTransformed = 0, bosReflagged = 0;
        var bosByIni = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // The hover's one-line account of what BOS did to a ref: record base -> effective base, the ini line, the
        // random flag, and which properties an override set.
        string BosLabelOf(BaseObjectSwaps.BosResolution res, FormKey rb)
        {
            string Ed(FormKey k) => baseEditorId.TryGetValue(k, out var e) ? e : k.ToString();
            var sb = new System.Text.StringBuilder("BOS ");
            sb.Append(res.Swapped ? $"{Ed(rb)} -> {Ed(res.EffectiveBase)} {res.EffectiveBase}" : $"{Ed(rb)} kept");
            if (res.SwapFrom is { } sf) sb.Append($" ({sf.Where.Ini}:{sf.Where.Line})");
            if (res.Random) sb.Append($" [random: {res.Alternatives.Count} alternatives, the first drawn{(res.PerSession ? ", re-rolled per session" : "")}]");
            if (res.Properties is { IsValid: true } p)
                sb.Append($" [{(p.Position is not null ? "pos " : "")}{(p.Rotation is not null ? "rot " : "")}{(p.Scale is not null ? "scale " : "")}{(p.FlagsSet != 0 || p.FlagsUnset != 0 ? "flags " : "")}"
                        + $"from {res.PropertiesFrom?.Ini}:{res.PropertiesFrom?.Line}{(res.PropertiesRandom ? ", a range — minimum drawn" : "")}]");
            return sb.ToString();
        }
        var meshCensus = listMeshes ? new Dictionary<string, (int Count, string Class)>(StringComparer.OrdinalIgnoreCase) : null;
        int stateArrived = 0, stateDeparted = 0;
        // Every walked ref's outcome in one word, for a host that lists the cells' placed refs (the workbench's
        // Records tree): drawn, or why not. Read nowhere else.
        refOutcome.Clear(); refUndrawnAt.Clear();
        string? BaseEd(PlacedRef wr) => wr.Base is { } b && baseEditorId.TryGetValue(b, out var e) ? e : null;
        foreach (var r in winRef.Values)
        {
            refOutcome[r.FormKey] = ("drawn", BaseEd(r));
            if (r.Deleted) { deletedSkipped++; refOutcome[r.FormKey] = ("deleted", null); continue; }
            // --state: what the flip changed for this ref — arrives (+1), departs (−1), nothing (0). A departed ref is
            // drawn only in the diff view, tinted, so the eye sees what left.
            int stateChange = 0;
            bool enabledNow = RefEnabled(r.FormKey);
            if (stateArm is not null)
            {
                bool was = RefEnabledBaseline(r.FormKey);
                stateChange = enabledNow && !was ? 1 : !enabledNow && was ? -1 : 0;
            }
            if (!enabledNow && !(stateChange == -1 && stateView == "diff"))
            {
                refOutcome[r.FormKey] = ("disabled", BaseEd(r));
                if (refEnableParent.ContainsKey(r.FormKey)) disabledParentSkipped++; else disabledFlagSkipped++;
                if (stateChange == -1) stateDeparted++;
                continue;
            }
            if (stateChange == 1) stateArrived++; else if (stateChange == -1) stateDeparted++;
            string? stateLabel = stateChange == 0 ? null : $"STATE {stateArm!.Name}: {(stateChange > 0 ? "arrives" : "departs")} with the flip of {StateRootLabel(r.FormKey)}";
            // The defect this counter watches: initially-disabled refs whose enable parent resolves
            // ENABLED at runtime — the standard way a mod swaps a whole set in. These used to be
            // silently dropped (a third of the Temple's content).
            if (r.InitiallyDisabled) enabledByParentDrawn++;
            if (r.Base is not { } bk0) { noModel++; refOutcome[r.FormKey] = ("no-base", null); continue; }
            // The Base Object Swapper layer: the base the game LOADS for this ref, at the transform its overrides
            // give it. A ref the layer flags initially-disabled at init is not drawn, and counted.
            var bk = bk0; var rPos = r.Position; var rRot = r.Rotation; var rScale = r.Scale;
            FormKey? bosRecordBase = null; string? bosLabel = null; bool bosRandom = false;
            if (bosLayer is not null && refCellOf.TryGetValue(r.FormKey, out var rCell))
            {
                var res = BaseObjectSwaps.Resolve(bosLayer, bosIdx!, r.FormKey, r.RecordType, bk0, rCell, r.EncounterZone);
                if (res.Swapped || res.Random || res.Properties is { IsValid: true })
                {
                    var (p2, rot2, sc2, dis2) = res.Apply(rPos, rRot, rScale, r.InitiallyDisabled);
                    // The own-flag half of the override was folded into refDisabledFlag before any chain was
                    // resolved (Q33), so presence has already been decided above by the same rule the engine uses.
                    // Filtering on dis2 here as well would re-apply it AFTER the chain, which is what dropped a
                    // reference the enable parent keeps.
                    if (dis2 != r.InitiallyDisabled) bosReflagged++;
                    rPos = p2; rRot = rot2; rScale = sc2;
                    if (res.Swapped)
                    {
                        bk = res.EffectiveBase; bosRecordBase = bk0; bosSwapped++;
                        if (res.SwapFrom is { } sf) bosByIni[sf.Where.Ini] = bosByIni.GetValueOrDefault(sf.Where.Ini) + 1;
                    }
                    if (res.Random) bosRandomCount++;
                    if (res.Properties is { IsValid: true }) bosTransformed++;
                    bosRandom = res.Random;
                    bosLabel = BosLabelOf(res, bk0);
                }
            }
            // --cell-lighting: every drawn LIGH reference is a point light (CellLighting.cs), model or not.
            if (ViewProbe.CellLighting.Requested && ViewProbe.CellLighting.Bases.ContainsKey(bk)) ViewProbe.CellLighting.Placed.Add((rPos, bk, r.FormKey));
            if (!baseMesh.TryGetValue(bk, out var rel)) { noModel++; refOutcome[r.FormKey] = ("no-model", BaseEd(r)); refUndrawnAt[r.FormKey] = (rPos, rRot); continue; }
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
            if (!keepFx && !fxStatic && isFx) { fxSkipped++; refOutcome[r.FormKey] = ("effect", BaseEd(r)); continue; }
            // Editor markers are not world geometry; the game never draws them. --markers keeps them
            // as a translucent extra layer instead of dropping them.
            if (isMarker && !showMarkers) { markerSkipped++; refOutcome[r.FormKey] = ("marker", BaseEd(r)); refUndrawnAt[r.FormKey] = (rPos, rRot); continue; }
            var explicitlyHighlighted = highlightColors.TryGetValue(r.FormKey, out var highlightColor);
            if (!explicitlyHighlighted && conflictDoors.Contains(r.FormKey)) highlightColor = new(1f,.35f,.10f);
            scene.Add(new ViewProbe.SceneRef(rel, rPos, rRot, rScale,
                                             explicitlyHighlighted || conflictDoors.Contains(r.FormKey), isMarker,
                                             r.FormKey, bk, baseEditorId.TryGetValue(bk, out var bEd) ? bEd : null,
                                             bosRecordBase, bosLabel, bosRandom, stateChange, stateLabel, constructedRefs.Contains(r.FormKey), highlightColor));
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
                           : baseMesh[bk0].Replace('/', (char)92).Contains("effects" + (char)92, StringComparison.OrdinalIgnoreCase) && !keepFx && !fxStatic ? "not drawn: FX (--no-fx)"
                           : r.InitiallyDisabled ? "drawn (enable parent resolves ENABLED)"
                           : "drawn";
                // base TYPE first, then FormKey, then EditorID or '-': a parser can take the row
                // by position, and "Door" is the word a caller closing doors is looking for.
                string bse = r.Base is { } bk1
                    ? (baseType.TryGetValue(bk1, out var bt) ? bt : "?") + " " + bk1
                      + " " + (baseEditorId.TryGetValue(bk1, out var be) ? be : "-")
                      + (baseMesh.TryGetValue(bk1, out var bm) ? $"  {bm}" : "")
                    : "(none)";
                Console.WriteLine($"      {d,6:0} u  {r.FormKey}  at {r.Position.X:0},{r.Position.Y:0},{r.Position.Z:0}"
                                + $"  scale {r.Scale:0.00}  [{why}]  base {bse}");
            }
        }
        foreach (var (qx, qy, qz) in insideAt)
        {
            // WHAT NOMINATES. This used to be the base record's OBND, and that is the same defect the
            // clearance broad phase carried: an authored box can UNDERSTATE its mesh, and then a ref
            // whose geometry really does enclose the eye is never nominated, never ray-tested, and the
            // eye reads "no physical ref's box encloses it" -- which the planner took as clearance
            // (Piranha, 2026-09-14). The mesh's own bounds come from the thing being tested, so they
            // are the conservative nomination. OBND survives as a COUNTED fallback for a ref whose
            // mesh cannot be read at all: such a ref cannot be ruled out, and an empty answer over a
            // population like that is not proven clearance. `--inside-bounds obnd` restores the old
            // nomination so the difference stays measurable rather than asserted.
            var hits = new List<(PlacedRef Ref, string Type, string? Edid, Aabb Box, bool FromNif, bool ObndToo)>();
            int obndOnlyNom = 0;
            foreach (var r in winRef.Values)
            {
                if (r.Deleted || DisabledWhy(r) is not null || r.Base is not { } bk) continue;
                if (!baseType.TryGetValue(bk, out var bt) || !insidePhysical.Contains(bt)) continue;
                if (baseIsMarker.Contains(bk)) continue;
                if (baseEditorId.TryGetValue(bk, out var ed) && IsControlEditorId(ed)) continue;
                if (baseMesh.TryGetValue(bk, out var bm0)
                    && Path.GetFileName(bm0).StartsWith("MarkerX", StringComparison.OrdinalIgnoreCase)) continue;
                Aabb obndWorld = Aabb.Empty;
                bool haveObnd = false;
                if (baseObnd.TryGetValue(bk, out var ob))
                {
                    var model = new Aabb(ob.Min, ob.Max, false);
                    if (!(model.Size.X <= 0 && model.Size.Y <= 0 && model.Size.Z <= 0))
                    {
                        obndWorld = SpatialOverlap.ToWorld(model, r.Position, r.Rotation, r.Scale);
                        haveObnd = !obndWorld.IsEmpty;
                    }
                }
                Aabb nifWorld = Aabb.Empty;
                bool haveNif = false;
                if (insideBounds != "obnd" && baseMesh.TryGetValue(bk, out var bmN)
                    && InsideMeshFor(bmN).Bounds is { IsEmpty: false } nb)
                {
                    nifWorld = SpatialOverlap.ToWorld(nb, r.Position, r.Rotation, r.Scale);
                    haveNif = !nifWorld.IsEmpty;
                }
                static bool Encloses(Aabb b, float x, float y, float z) =>
                    !b.IsEmpty && x >= b.Min.X && x <= b.Max.X && y >= b.Min.Y && y <= b.Max.Y && z >= b.Min.Z && z <= b.Max.Z;
                bool obndSays = haveObnd && Encloses(obndWorld, qx, qy, qz);
                if (haveNif)
                {
                    if (!Encloses(nifWorld, qx, qy, qz)) continue;
                    hits.Add((r, bt, ed, nifWorld, true, obndSays));
                }
                else
                {
                    // No mesh bounds: the record box is the only thing left to nominate with, and
                    // nobody checked it against the geometry. Nominating on it is still better than
                    // dropping the ref, but the fact that it happened is reported.
                    if (!obndSays) continue;
                    if (insideBounds != "obnd" && !(baseMesh.TryGetValue(bk, out var nm0) && InsideNoSolidShape(nm0))) obndOnlyNom++;
                    hits.Add((r, bt, ed, obndWorld, false, true));
                }
            }
            // A ref whose mesh could not be read is a ref this query could NOT rule out. The count
            // travels with the answer -- including with an EMPTY answer, which is the case the
            // planner used to read as clearance.
            int unreadable = 0;
            foreach (var r in winRef.Values)
            {
                if (insideBounds == "obnd") break;
                if (r.Deleted || DisabledWhy(r) is not null || r.Base is not { } bk) continue;
                if (!baseType.TryGetValue(bk, out var bt2) || !insidePhysical.Contains(bt2)) continue;
                if (baseIsMarker.Contains(bk)) continue;
                if (baseEditorId.TryGetValue(bk, out var ed2) && IsControlEditorId(ed2)) continue;
                if (!baseMesh.TryGetValue(bk, out var bmU)) continue;
                if (Path.GetFileName(bmU).StartsWith("MarkerX", StringComparison.OrdinalIgnoreCase)) continue;
                if (InsideMeshFor(bmU).Bounds is null && !InsideNoSolidShape(bmU)) unreadable++;
            }
            string nomination = insideBounds == "obnd"
                ? "nominated by RECORD OBND (the legacy arm, kept for comparison: a box that understates its mesh nominates nothing)"
                : $"nominated by mesh bounds, {obndOnlyNom} by record OBND alone (mesh unreadable), {unreadable} physical ref(s) whose mesh could not be read at all";
            // A ref drawn only by effect-shader shapes encloses without being solid: listed, never tested.
            var effectOnlyHits = hits.Where(h => baseMesh.TryGetValue(h.Ref.Base!.Value, out var em) && InsideEffectOnly(em)).ToList();
            hits.RemoveAll(h => effectOnlyHits.Contains(h));
            if (effectOnlyHits.Count > 0)
                nomination += $"; {effectOnlyHits.Count} effect-only ref(s) also enclose it, not solid (drawn geometry only: an effect shape's own collision is not tested)";
            // A ref with no solid shape for another reason (hidden or refraction shapes among the rest): listed, never tested.
            var noSolidHits = hits.Where(h => baseMesh.TryGetValue(h.Ref.Base!.Value, out var nm) && InsideNoSolidShape(nm)).ToList();
            hits.RemoveAll(h => noSolidHits.Contains(h));
            if (noSolidHits.Count > 0)
                nomination += $"; {noSolidHits.Count} ref(s) with no solid shape (hidden, refraction or effect shapes only) also enclose it, not solid";
            void PrintEffectOnly()
            {
                foreach (var h in effectOnlyHits)
                    Console.WriteLine($"      effect-only, not solid: {h.Ref.FormKey}  {h.Edid ?? "-"}"
                                    + (baseMesh.TryGetValue(h.Ref.Base!.Value, out var em2) ? $"  mesh {em2}" : ""));
                foreach (var h in noSolidHits)
                    Console.WriteLine($"      no solid shape, not solid: {h.Ref.FormKey}  {h.Edid ?? "-"}"
                                    + (baseMesh.TryGetValue(h.Ref.Base!.Value, out var nm2) ? $"  mesh {nm2}" : ""));
            }
            if (hits.Count == 0)
            {
                Console.WriteLine($"   inside-at {qx:0},{qy:0},{qz:0}: no physical ref's box encloses it; {nomination}");
                PrintEffectOnly();
                continue;
            }
            Console.WriteLine($"   inside-at {qx:0},{qy:0},{qz:0}: {hits.Count} ref(s) whose box encloses it (smallest first); {nomination}");
            PrintEffectOnly();
            foreach (var (r, t, ed, box, fromNif, obndToo) in hits.OrderBy(h => MathF.Max(h.Box.Size.X, MathF.Max(h.Box.Size.Y, h.Box.Size.Z))))
            {
                var s = box.Size;
                string mesh = baseMesh.TryGetValue(r.Base!.Value, out var bm2) ? bm2 : "";
                string rays = mesh.Length > 0 ? InsideRays(r, mesh, qx, qy, qz) : "rays n/a (no mesh)";
                Console.WriteLine($"      {r.FormKey}  {t}  {ed ?? "-"}  dims {s.X:0}x{s.Y:0}x{s.Z:0} u"
                                + $"  at {r.Position.X:0},{r.Position.Y:0},{r.Position.Z:0}  scale {r.Scale:0.00}"
                                + $"  {rays}"
                                + $"  [{(fromNif ? "mesh bounds" : "record OBND, mesh unreadable")}"
                                + $"; record box {(obndToo ? "agrees" : "would NOT have nominated this ref")}]"
                                + (mesh.Length > 0 ? $"  mesh {mesh}" : ""));
            }
        }

        // Every ref the scene drops is accounted for HERE, in one block — a silent category is how a
        // render missing a third of its content once read as fully accounted for.
        Console.WriteLine($"   refs not drawn: {disabledParentSkipped:N0} disabled via enable parent, "
                        + $"{disabledFlagSkipped:N0} initially disabled (own flag, no parent), "
                        + $"{deletedSkipped:N0} deleted, {noModel:N0} with no model-bearing base");
        if (enabledByParentDrawn > 0)
            Console.WriteLine($"   {enabledByParentDrawn:N0} initially-disabled ref(s) DRAWN — their enable parent resolves ENABLED at runtime");
        if (stateArm is not null)
            Console.WriteLine($"   STATE {stateArm.Name} {stateView}: {stateArrived:N0} ref(s) in this cell arrive with the flip (drawn{(stateView == "diff" ? ", tinted GREEN" : "")}), "
                            + $"{stateDeparted:N0} depart ({(stateView == "diff" ? "drawn tinted RED" : "not drawn")})");
        if (bosLayer is not null)
        {
            Console.WriteLine($"   BOS {bosMode}: {bosSwapped:N0} drawn ref(s) carry a swapped base ({bosRandomCount:N0} random — the first alternative is drawn), "
                            + $"{bosTransformed:N0} with a property override, {bosReflagged:N0} whose own initially-disabled flag an override changes (folded into the enable chain before it resolved, so a reference with an enable parent keeps the parent's answer — Q33)");
            foreach (var kv in bosByIni.OrderByDescending(k => k.Value).Take(10)) Console.WriteLine($"      {kv.Value,6:N0}  swaps from {kv.Key}");
        }
        else Console.WriteLine("   BOS off: record bases (--bos on|diff for the bases the game loads)");
        if (fxSkipped > 0) Console.WriteLine($"   ({fxSkipped} effect-mesh refs skipped by --no-fx; --fx or the default draws them)");
        if (markerSkipped > 0) Console.WriteLine($"   ({markerSkipped} editor-marker refs skipped; --markers to include them translucently)");
        if (meshCensus is not null)
        {
            Console.WriteLine($"   ---- distinct meshes in this cell ({meshCensus.Count}) ----");
            foreach (var kv in meshCensus.OrderByDescending(k => k.Value.Count))
                Console.WriteLine($"   {kv.Value.Count,5}  {kv.Value.Class,-8}  {kv.Key}");
        }

        // --nav-z's storey test, in ONE place: the sheet, the wire, the dashed hidden layers and
        // the fly-through's hover list all ask this and cannot disagree about which triangles the
        // picture contains. True for every triangle when the flag was not given.
        //
        // The triangle's own z RANGE against the band, not its centroid: a staircase's bottom step
        // belongs to the floor it rises from, and a centroid test drops it as soon as the step is
        // taller than the tolerance. A triangle spanning two storeys is kept on both — it does.
        bool NavZKeeps(NavmeshGeometry nav, NavTri tri)
        {
            if (navZAt is not { } z0) return true;
            float az = nav.Vertices[tri.A].Z, bz = nav.Vertices[tri.B].Z, cz = nav.Vertices[tri.C].Z;
            return MathF.Max(az, MathF.Max(bz, cz)) >= z0 - navZTol
                && MathF.Min(az, MathF.Min(bz, cz)) <= z0 + navZTol;
        }

        // One navmesh SOURCE -> (green context, red finding) triangle sets. The red spec is applied
        // per source, so a single-triangle red lands on THAT version's triangle — in a compare, the
        // same record can be red-whole in every panel while its shape changes between them.
        var navColourGroups = new List<ViewProbe.NavColourGroup>();
        (List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)> Green,
         List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)> Red)
        BuildNavSets(IReadOnlyDictionary<FormKey, NavmeshGeometry> navs)
        {
            navColourGroups.Clear();
            var groups = new Dictionary<string, List<(Vector3, Vector3, Vector3)>>();
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
                    if (!ViewProbe.NavmeshSelection.IsDrawable(nav, ti)) continue;
                    if (!NavZKeeps(nav, tri)) continue;
                    var t3 = (V(nav.Vertices[tri.A]), V(nav.Vertices[tri.B]), V(nav.Vertices[tri.C]));
                    if (wholeRed || (reds is not null && reds.Contains(ti))) red.Add(t3);
                    else
                    {
                        green.Add(t3);
                        var colour = navSelection.Cells[navSelection.Provenance[nav].Cell] ?? "default";
                        if (!groups.TryGetValue(colour, out var group)) groups[colour] = group = new();
                        group.Add(t3);
                    }
                }
            }
            foreach (var (colour, triangles) in groups) navColourGroups.Add(new(colour, triangles));
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
                int ltexIndex = -1;
                foreach (var path in order.OrderedPaths)
                {
                    ltexIndex++;
                    ISkyrimModGetter ms = ViewProbe.PluginRead.Open(path, ltexIndex, order.OrderedPaths.Count, "--find-ltex");
                    string msName = Path.GetFileName(path);
                    try
                    {
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
                                int painted = 0; bool isBase = false; int layersRead = 0;
                                try
                                {
                                    foreach (var layer in ls.Layers)
                                    {
                                        // counted when its reads finish, so a throw from the enumerator or the body
                                        // reports exactly the layers read before it
                                        if (layer.Header?.Texture.FormKeyNullable is { } lk && wanted.ContainsKey(lk))
                                        {
                                            if (layer is IAlphaLayerGetter al)
                                                painted += ViewProbe.TerrainLand.AlphaPoints(al).Count(pt => pt.Opacity > 0.004f);
                                            else { isBase = true; painted += 289; }
                                        }
                                        layersRead++;
                                    }
                                }
                                // A layer that cannot be read is counted and named, not swallowed (PluginRead.cs).
                                catch (Exception ex) { ViewProbe.TerrainLayerFailures.Add(msName, gp.X, gp.Y, layersRead, ex, "--find-ltex"); }
                                // A later plugin's LAND for the same cell REPLACES the earlier one, so
                                // overwrite rather than accumulate — same winner rule as the terrain walk.
                                if (painted > 0 || isBase) hits[(gp.X, gp.Y)] = (painted, isBase, c.FormKey, c.EditorID);
                                else hits.Remove((gp.X, gp.Y));
                            }
                        }
                    }
                    catch (Exception ex) when (ViewProbe.PluginRead.Wraps(ex))
                    { throw ViewProbe.PluginRead.Failed(path, ltexIndex, order.OrderedPaths.Count, "--find-ltex", ex); }
                }
                Console.WriteLine($"   ---- cells painting it, top 20 of {hits.Count} ----");
                foreach (var ((gx, gy), (painted, isBase, ck, ced)) in hits.OrderByDescending(k => k.Value.Painted).Take(20))
                    Console.WriteLine($"      cell ({gx,4},{gy,4})  {painted,5} painted point(s){(isBase ? "  (BASE)" : "      ")}"
                                    + $"  centre {gx * 4096 + 2048},{gy * 4096 + 2048}"
                                    + $"  {ck}{(string.IsNullOrWhiteSpace(ced) ? "" : "  " + ced)}");
            }
            return 0;
        }

        // Terrain layers that could not be read belong to the walk that read them: a retained walk keeps its list.
        if (!retainedWalkHit) ViewProbe.TerrainLayerFailures.Reset();
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
        // What the native terrain reads beyond the sheets: LAND normals / colours per square and
        // each landscape diffuse's TXST normal map.
        var terrainExtras = new ViewProbe.NativeTerrainExtras();
        // --interactive: hovering bare ground answers with the quadrant's painted layers. Assigned
        // inside the terrain block (it captures the walked LAND census); null on interiors.
        Func<float, float, string?>? terrainDescribeAt = null;
        // Grass grows only from an exterior's land: an interior shot after an exterior one must not keep its cells.
        if (interior || targetGrid is null) { ViewProbe.GrassLayer.Cells.Clear(); ViewProbe.GrassLayer.WaterHeights.Clear(); }
        // Reuse is restricted above to static worklists. Interactive terrain hover needs the
        // LAND layer census callback and therefore cannot enter this retained branch.
        if (retainedWalkHit && (drawTerrain || drawWater) && !interior && targetGrid is not null)
        {
            // The retained walk keeps the terrain sheets, not each layer's LTEX: no grass lists to grow from.
            ViewProbe.GrassLayer.NoCellsWhy = "a retained walk keeps no landscape-texture identities (run without the reuse options)";
            foreach (var p in retainedWalk!.TerrainPatches)
                terrainPatches[p.Diffuse] = p.Triangles.ToList();
            foreach (var l in retainedWalk.TerrainLayers)
                terrainLayers.Add((l.Diffuse, l.Triangles.ToList()));
            foreach (var h in retainedWalk.LandHeights)
            {
                landHeights[(h.X, h.Y)] = (float[,])h.Heights.Clone();
                if (h.Normals is { } hn) terrainExtras.Normals[(h.X, h.Y)] = hn.ToArray();
                if (h.Colors is { } hc) terrainExtras.Colors[(h.X, h.Y)] = hc.ToArray();
            }
            int terrTotal = terrainPatches.Values.Sum(v => v.Count);
            if (terrTotal > 0)
                Console.WriteLine($"   terrain: {landHeights.Count} cell(s) of LAND, {terrTotal:N0} triangles, "
                                + $"{terrainPatches.Count(kv => kv.Key.Length > 0)} distinct ground texture(s)");
            else
                Console.WriteLine("   terrain: no LAND found for this cell (--no-terrain to silence)");
            if (terrainLayers.Count > 0)
                Console.WriteLine($"   terrain paint layers: {terrainLayers.Count} sheet(s), "
                                + $"{terrainLayers.Sum(l => l.Tris.Count):N0} triangles, "
                                + $"{terrainLayers.Select(l => l.Diffuse).Distinct(StringComparer.OrdinalIgnoreCase).Count()} distinct texture(s)");
        }
        // Water rides this same cell walk (it is where CELL.WaterHeight is read), so the walk has
        // to run whenever EITHER is wanted — otherwise --no-terrain silently takes the sea with it,
        // which is a coupling nobody would guess from the flag's name.
        if (!retainedWalkHit && (drawTerrain || drawWater) && !interior && targetGrid is { } tg)
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
            int terrIndex = -1;
            foreach (var path in order.OrderedPaths)
            {
                ViewProbe.RenderStages.Tick?.Invoke();
                terrIndex++;
                // Refused, not skipped (PluginRead.cs): a later plugin's LAND and water would silently lose to an earlier one.
                ISkyrimModGetter m2 = ViewProbe.PluginRead.Open(path, terrIndex, order.OrderedPaths.Count, "the terrain walk");
                string m2Name = Path.GetFileName(path);
                try
                {
                using (m2 as IDisposable)
                {
                    bool truthPlugin = HarnessShared.PluginKinds.ByName(Path.GetFileName(path)) is HarnessShared.PluginKind.Bethesda or HarnessShared.PluginKind.Ussep;
                    foreach (var ws in m2.Worldspaces.Records)
                    {
                        // --stations: EVERY worldspace's default AS READ (sentinels kept — the one
                        // resolver classifies them, this does not) and its parent link, taken
                        // BEFORE the target filter, because the chain a city worldspace defers
                        // along runs through worldspaces that are not the target.
                        if (stationsSpec is not null)
                            worldChain[ws.FormKey.ToString()] =
                                (ws.LandDefaults?.DefaultWaterHeight,
                                 ws.Parent?.Worldspace.FormKeyNullable is { IsNull: false } wp
                                     ? wp.ToString() : null);
                        if (ws.FormKey != targetWorldspace) continue;
                        if (ws.EditorID is { Length: > 0 } wsed) stationWorldspaceEd = wsed;
                        // The worldspace's own water, and the height its cells fall back to. Read
                        // per plugin in load order, so the last one to state either of them wins.
                        if (ws.Water.FormKeyNullable is { } wsw && !wsw.IsNull) wsWater = wsw;
                        if (ws.LandDefaults?.DefaultWaterHeight is { } dwh && RealHeight(dwh)) wsDefaultWaterHeight = dwh;
                        if (truthPlugin)
                        {
                            if (ws.Water.FormKeyNullable is { } wswt && !wswt.IsNull) wsWaterTruth = wswt;
                            if (ws.LandDefaults?.DefaultWaterHeight is { } dwht && RealHeight(dwht)) wsDefaultWaterHeightTruth = dwht;
                        }
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
                            bool inVisible = visibleSet is not null && visibleSet.Contains((gp.X, gp.Y));
                            if (!inVisible && (Math.Abs(gp.X - tg.X) > stationCellRadius
                                || Math.Abs(gp.Y - tg.Y) > stationCellRadius)) continue;
                            // WHICH cell this grid square is, for --stations: a pose names the cell
                            // the STATION stands in, and one ladder reaches across a cell border.
                            // In load order, so the last plugin to state the square wins it.
                            if (stationsSpec is not null) cellAtGrid[(gp.X, gp.Y)] = (c.FormKey, c.EditorID);
                            // Water is harvested BEFORE the LAND gate below: a cell can be all
                            // sea with no heightmap at all, and that is exactly the cell whose
                            // water matters most.
                            cellWater[(gp.X, gp.Y)] = (c.Flags.HasFlag(Cell.Flag.HasWater),
                                                       c.WaterHeight ?? float.MaxValue,
                                                       c.Water.FormKeyNullable is { } cw && !cw.IsNull ? cw : null);
                            if (truthPlugin) cellWaterTruth[(gp.X, gp.Y)] = cellWater[(gp.X, gp.Y)];
                            // The TERRAIN stays at ring 1 whatever the water radius is: it is the
                            // renderer's, and --stations gets its heightmaps from FloorFeed's own
                            // filing instead. Widening it would decode a 7x7 block of LAND for a
                            // mode that draws nothing.
                            if (visibleSet is not null ? !inVisible : Math.Abs(gp.X - tg.X) > 1 || Math.Abs(gp.Y - tg.Y) > 1) continue;
                            // One rule (TerrainLand.cs, the core's LandHeight.Classify): no LAND leaves the earlier
                            // terrain; a LAND that declares no heights removes it, so nothing is drawn there (the engine
                            // draws no ground); an unreadable one is refused (skipping it drew the earlier plugin's heights).
                            if (ViewProbe.TerrainLand.Step(c.Landscape, m2Name, (gp.X, gp.Y), landWin, landLayers, landPromoted,
                                    terrainExtras.Normals, terrainExtras.Colors) is not { } lcT) continue;
                            terrainExtras.TakeLand(c.Landscape!, (gp.X, gp.Y));
                            var grad = lcT.Gradients!;
                            // Base layer per quadrant (a non-alpha layer names the quadrant's ground
                            // texture; alpha layers blend on top and are out of scope for this pass).
                            var quad = new FormKey?[4];
                            var census = new List<(int Quadrant, int Layer, bool Alpha, FormKey? Tex, int Painted, float MaxOpacity, float[]? Opacity)>();
                            var promoted = new[] { -1, -1, -1, -1 };
                            try
                            {
                                foreach (var layer in c.Landscape!.Layers)
                                {
                                    int qi = layer.Header is { } lh ? (int)lh.Quadrant : -1;
                                    var tex = layer.Header?.Texture.FormKeyNullable;
                                    if (layer is IAlphaLayerGetter al)
                                    {
                                        // 17x17 opacity points per quadrant, addressed by Position.
                                        var op = new float[289];
                                        int painted = 0; float maxOp = 0f;
                                        // An ATXT with no VTXT after it reads as null data: it paints nothing.
                                        foreach (var pt in ViewProbe.TerrainLand.AlphaPoints(al))
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
                                // No base layer for a quadrant: the engine draws its default landscape
                                // texture there (QuadDiffuse below; OpenMW's ESM4 loader starts every
                                // quadrant's base at form 0 for the same reason). VP_TERRAIN_PROMOTE=1 keeps
                                // the older stand-in for comparison: the alpha layer that paints the most of
                                // the quadrant, remembered so it is not also drawn on top of itself. Gated on
                                // the same switch as the overlays — --no-terrain-layers means "ignore the
                                // alpha layers entirely", base stand-ins included.
                                bool promoteStandIn = Environment.GetEnvironmentVariable("VP_TERRAIN_PROMOTE") == "1";
                                for (int q = 0; promoteStandIn && drawTerrainLayers && q < 4; q++)
                                {
                                    if (quad[q] is not null) continue;
                                    var best = census.Where(l => l.Alpha && l.Quadrant == q && l.Tex is not null)
                                                     .OrderByDescending(l => l.Painted).ToList();
                                    if (best.Count > 0 && best[0].Tex is { } bt) { quad[q] = bt; promoted[q] = best[0].Layer; }
                                }
                            }
                            // A texture layer that cannot be read changes paint, not ground: counted and named on the
                            // console and in the report (PluginRead.cs), and the layers read before it are kept.
                            catch (Exception ex) { ViewProbe.TerrainLayerFailures.Add(m2Name, gp.X, gp.Y, census.Count, ex, "the terrain walk"); }
                            landWin[(gp.X, gp.Y)] = (lcT.Offset, grad, quad);   // later plugin overwrites = wins
                            landLayers[(gp.X, gp.Y)] = census;
                            landPromoted[(gp.X, gp.Y)] = promoted;
                        }
                    }
                }
                }
                catch (Exception ex) when (ViewProbe.PluginRead.Wraps(ex))
                { throw ViewProbe.PluginRead.Failed(path, terrIndex, order.OrderedPaths.Count, "the terrain walk", ex); }
            }

            // The engine's default landscape texture (its sDefaultLandDiffuseTexture setting): what it draws for a
            // quadrant with no BTXT and for a BTXT or ATXT whose texture reference is zero (UESP, LAND: "If the
            // reference is zero the engine falls back to dirt02.dds"; OpenMW's ESM4 loader, the same for Skyrim).
            // Its normal map is found like any other diffuse's, through the TXST that names it (NormalForDiffuse).
            const string DefaultLandDiffuse = @"textures\landscape\dirt02.dds";
            string? QuadDiffuse(FormKey? ltex)
            {
                if (ltex is not { } lk || lk.IsNull) return DefaultLandDiffuse;
                if (!ltexToTxst.TryGetValue(lk, out var ts)) return null;
                if (!txstDiffuse.TryGetValue(ts, out var d)) return null;
                var p = d.Replace('/', '\\');
                return p.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) ? p : "textures\\" + p;
            }

            int promotedQuads = 0;
            foreach (var pr in landPromoted.Values) promotedQuads += pr.Count(v => v >= 0);
            if (promotedQuads > 0)
                Console.WriteLine($"   terrain: {promotedQuads} quadrant(s) carry no base layer — "
                                + $"stood in with their most-painted alpha layer (VP_TERRAIN_PROMOTE)");
            {
                int noBase = 0, zeroRef = 0;
                foreach (var (grid, census) in landLayers)
                    for (int q = 0; q < 4; q++)
                    {
                        var bl = census.Where(l => !l.Alpha && l.Quadrant == q).ToList();
                        if (bl.Count == 0) { if (!landPromoted.TryGetValue(grid, out var pr) || pr[q] < 0) noBase++; }
                        else if (bl[0].Tex is not { } bk || bk.IsNull) zeroRef++;
                    }
                int zeroAlpha = landLayers.Values.Sum(c => c.Count(l => l.Alpha && (l.Tex is not { } ak || ak.IsNull)));
                if (noBase + zeroRef + zeroAlpha > 0)
                    Console.WriteLine($"   terrain: default landscape texture ({DefaultLandDiffuse}) drawn for {noBase} quadrant(s) "
                                    + $"with no base layer, {zeroRef} base layer(s) and {zeroAlpha} alpha layer(s) whose texture is zero");
            }

            foreach (var ((gx, gy), (off, grad, quadLtex)) in landWin)
            {
                // VHGT decode — HousecarlCore.LandHeight (moved there 2026-09-04 so the landscape-seam detector and
                // this renderer read one function): world units, [x, y].
                var h = HousecarlCore.LandHeight.Decode(off, grad);
                landHeights[(gx, gy)] = h;
                System.Numerics.Vector3 P(int x, int y) =>
                    new(gx * 4096f + x * 128f, gy * 4096f + y * 128f, h[x, y]);
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
            // ---- GRASS (GrassLayer.cs): one TerrainCell per drawn cell, each quadrant's base and alpha layers
            // named by their LTEX so each carries that texture's grass list (LTEX GNAM), in the terrain's blend
            // order; the water height each cell's grass water rule tests (the cell's own, else the worldspace
            // default; none for a cell without water).
            ViewProbe.GrassLayer.Cells.Clear();
            ViewProbe.GrassLayer.WaterHeights.Clear();
            if (ViewProbe.GrassLayer.Requested && drawTerrain)
                foreach (var ((gx, gy), _) in landWin.OrderBy(k => k.Key.Y).ThenBy(k => k.Key.X))
                {
                    var hgt = landHeights[(gx, gy)];
                    var flat = new float[SkyRender.World.TerrainCell.Side * SkyRender.World.TerrainCell.Side];
                    for (int y = 0; y < SkyRender.World.TerrainCell.Side; y++)
                        for (int x = 0; x < SkyRender.World.TerrainCell.Side; x++)
                            flat[y * SkyRender.World.TerrainCell.Side + x] = hgt[x, y];
                    var gcell = new SkyRender.World.TerrainCell(gx, gy, flat)
                    {
                        Normals = terrainExtras.Normals.TryGetValue((gx, gy), out var gn) ? gn : null,
                        Colors = terrainExtras.Colors.TryGetValue((gx, gy), out var gc) ? gc : null,
                    };
                    var census = landLayers.TryGetValue((gx, gy), out var cl) ? cl : new();
                    for (int q = 0; q < 4; q++)
                    {
                        var baseLayer = census.Where(l => !l.Alpha && l.Quadrant == q).Select(l => l.Tex).FirstOrDefault();
                        var alpha = census.Where(l => l.Alpha && l.Quadrant == q && l.Opacity is not null).OrderBy(l => l.Layer)
                            .Select(l => new SkyRender.World.TerrainLayer(
                                new SkyRender.World.TerrainTexture(QuadDiffuse(l.Tex) ?? "") { Grasses = ViewProbe.GrassLayer.GrassesFor(l.Tex) },
                                l.Opacity!))
                            .ToList();
                        gcell.Quadrants[q] = new SkyRender.World.TerrainQuadrant
                        {
                            Base = new SkyRender.World.TerrainTexture(QuadDiffuse(baseLayer) ?? "") { Grasses = ViewProbe.GrassLayer.GrassesFor(baseLayer) },
                            Layers = alpha,
                        };
                    }
                    ViewProbe.GrassLayer.Cells.Add(gcell);
                    if (cellWater.TryGetValue((gx, gy), out var cw) && cw.Has)
                    {
                        float wh = cw.Height != float.MaxValue ? cw.Height : wsDefaultWaterHeight;
                        if (wh != float.MaxValue) ViewProbe.GrassLayer.WaterHeights[(gx, gy)] = wh;
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
            if (winRefsDump is not null)
            {
                ViewProbe.WinningRefsDump.Write(winRefsDump, cellTarget, cellKey!.Value, interior, targetWorldspace, targetGrid, ringRefs,
                    order.OrderedPaths, winRef, refCellOf, persistentWin, winWalk.TakenOutKeys(), landHeights,
                    landLayers.ToDictionary(kv => kv.Key, kv => kv.Value.Count));
                return 0;
            }

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
        // Each landscape diffuse's normal map (TXST NormalOrGloss), keyed by the diffuse path in the
        // same normalised form QuadDiffuse gives the sheets. Fresh and retained walks both have
        // txstDiffuse / txstNormal by now.
        static string TexPath(string p)
        {
            p = p.Replace('/', '\\');
            return p.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) ? p : "textures\\" + p;
        }
        foreach (var (tsKey, tsDiffuse) in txstDiffuse)
            if (txstNormal.TryGetValue(tsKey, out var tsNormal))
                terrainExtras.NormalForDiffuse.TryAdd(TexPath(tsDiffuse), TexPath(tsNormal));
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
                return z;
            };

        foreach (var (qx, qy, qz) in clearAt)
        {
            var o = new Vector3(qx, qy, qz);
            var dirs = new (string Name, Vector3 D)[]
            {
                ("+x", Vector3.UnitX), ("-x", -Vector3.UnitX), ("+y", Vector3.UnitY), ("-y", -Vector3.UnitY),
                ("up", Vector3.UnitZ), ("down", -Vector3.UnitZ),
            };
            var best = new float?[dirs.Length];
            var owner = new PlacedRef?[dirs.Length];
            var groundIsNearest = new bool[dirs.Length];
            var sweep = Aabb.Empty;
            foreach (var d in dirs) { sweep = sweep.Add(o); sweep = sweep.Add(o + d.D * clearReach); }
            // Three populations, all reported: what the broad phase offered, what carried readable
            // geometry, and what it could not see at all. The last one is the honest limit — a ref
            // with no usable OBND never reaches the ray test, so "nothing near this point" is a
            // statement about the boxes as much as about the meshes.
            int candidates = 0, meshed = 0, unread = 0, noBox = 0, noMesh = 0, obndOnly = 0, obndWouldMiss = 0, effectOnly = 0, noSolid = 0;
            foreach (var r in winRef.Values)
            {
                if (r.Deleted || DisabledWhy(r) is not null || r.Base is not { } bk) continue;
                if (!baseType.TryGetValue(bk, out var bt) || !insidePhysical.Contains(bt)) continue;
                if (baseIsMarker.Contains(bk)) continue;
                if (baseEditorId.TryGetValue(bk, out var ed) && IsControlEditorId(ed)) continue;
                if (!baseMesh.TryGetValue(bk, out var bm)) { noMesh++; continue; }
                if (Path.GetFileName(bm).StartsWith("MarkerX", StringComparison.OrdinalIgnoreCase)) continue;
                // WHICH BOX SELECTS A CANDIDATE. Record OBND is authored data and it can be present
                // and WRONG -- undersized, or offset from the mesh it claims to bound. A wrong box
                // rejects the ref here, silently, and never touches the missing-box count, so an
                // answer of "nothing found" over "zero coverage gaps" proved nothing about the
                // geometry (reported by Piranha against the first version of this probe). The mesh's
                // OWN bounds are the conservative choice: they come from the thing being tested
                // rather than from a record describing it, and they survive the triangle cap, so an
                // over-cap ref is admitted as a candidate and then reported UNREADABLE rather than
                // dropped. OBND is the fallback when the mesh cannot be read at all, and that
                // fallback is COUNTED -- a ref selected by a box nobody verified is a coverage
                // caveat, not a clean answer. `--clear-bounds obnd` restores the old filter so the
                // difference stays measurable rather than asserted.
                var nifBounds = clearBounds == "obnd" ? null : InsideMeshFor(bm).Bounds;
                Aabb obndWorld = Aabb.Empty;
                bool haveObnd = false;
                if (baseObnd.TryGetValue(bk, out var ob))
                {
                    var model = new Aabb(ob.Min, ob.Max, false);
                    if (!(model.Size.X <= 0 && model.Size.Y <= 0 && model.Size.Z <= 0))
                    {
                        obndWorld = SpatialOverlap.ToWorld(model, r.Position, r.Rotation, r.Scale);
                        haveObnd = !obndWorld.IsEmpty;
                    }
                }
                Aabb box;
                bool fromNif = nifBounds is { IsEmpty: false };
                if (fromNif)
                    box = SpatialOverlap.ToWorld(nifBounds!.Value, r.Position, r.Rotation, r.Scale);
                // Only a FALLBACK counts: in the `obnd` arm every ref is selected that way by
                // design, and labelling 555 refs "their mesh could not be read" would be a false
                // coverage caveat in the arm that exists for comparison.
                else if (haveObnd) { box = obndWorld; if (clearBounds != "obnd" && !InsideNoSolidShape(bm)) obndOnly++; }
                else { noBox++; continue; }
                if (box.IsEmpty) { noBox++; continue; }
                if (SpatialOverlap.BoxDistance(box, sweep) > 0f) continue;
                // Admitted. Would the record's own box have admitted it too? The count is the
                // measurement behind the claim, one per query rather than a survey.
                if (InsideEffectOnly(bm)) { effectOnly++; continue; }   // drawn only by effect shaders: not solid
                if (InsideNoSolidShape(bm)) { noSolid++; continue; }    // no solid shape (hidden, refraction or effect only)
                if (fromNif && (!haveObnd || SpatialOverlap.BoxDistance(obndWorld, sweep) > 0f)) obndWouldMiss++;
                candidates++;
                if (InsideMeshFor(bm).Soup is not { } soup) { unread++; continue; }
                meshed++;
                var world = SpatialOverlap.TransformSoup(soup, r.Position, r.Rotation, r.Scale);
                for (int k = 0; k < dirs.Length; k++)
                    if (NearestHit(o, dirs[k].D, world.Vertices, world.Indices, best[k] ?? clearReach) is { } h
                        && (best[k] is null || h < best[k]))
                    { best[k] = h; owner[k] = r; }
            }
            // TERRAIN. Outdoors the ground is not a placed reference — it is the LAND heightmap the
            // renderer builds separately — so a probe that casts only against refs reports "nothing
            // below" for a camera standing on a hillside. That is a WRONG answer, not a missing one,
            // and it would refuse every exterior vantage. The height field is a function of XY, so
            // the two vertical rays read it directly rather than marching it; the four lateral rays
            // do NOT, and the line says so. Which of the three terrain states applies is printed,
            // because "no ground here" and "no ground DATA here" are different facts and only the
            // first is evidence of open space.
            string terrainNote;
            float? groundZ = null;
            if (interior)
                terrainNote = "terrain: interior cell, none expected";
            else if (terrainHeightAt is null)
                terrainNote = "terrain: NOT BUILT for this run (--no-terrain, or no LAND data loaded) "
                            + "— absence below this eye is not evidence outdoors";
            else if (terrainHeightAt(qx, qy) is not { } gz)
                terrainNote = "terrain: NO heightmap covers this XY — absence below this eye is not evidence";
            else
            {
                groundZ = gz;
                terrainNote = $"terrain: ground at z {gz:0.0}";
                float dz = qz - gz;
                if (dz > 1e-3f && dz <= clearReach && (best[5] is null || dz < best[5]))
                { best[5] = dz; owner[5] = null; groundIsNearest[5] = true; }
                float uz = gz - qz;
                if (uz > 1e-3f && uz <= clearReach && (best[4] is null || uz < best[4]))
                { best[4] = uz; owner[4] = null; groundIsNearest[4] = true; }
            }
            Console.WriteLine($"   clear-at {qx:0},{qy:0},{qz:0}: nearest placed geometry on each world axis "
                            + $"within {clearReach:0} u — {candidates} candidate(s) in the broad phase, {meshed} with a "
                            + $"readable mesh, {unread} unreadable, {noBox} physical ref(s) skipped for want of a usable OBND, "
                            + $"{noMesh} physical ref(s) with no model at all, {obndOnly} selected by record OBND "
                            + $"because their mesh could not be read, {obndWouldMiss} admitted by mesh bounds that the "
                            + $"record box alone would have excluded; {terrainNote} "
                            + $"(broad phase: {(clearBounds == "obnd" ? "RECORD OBND \u2014 the legacy arm, kept for comparison; a wrong box drops a ref silently" : "the mesh's own NIF bounds, with record OBND only as a counted fallback")}; "
                            + "the four lateral rays ignore terrain)"
                            + $"; {effectOnly} effect-only ref(s) in reach not tested, not solid (drawn geometry only: an effect shape's own collision is not tested)"
                            + (noSolid > 0 ? $"; {noSolid} ref(s) in reach with no solid shape (hidden, refraction or effect shapes only) not tested, not solid" : ""));
            for (int k = 0; k < dirs.Length; k++)
            {
                string who = owner[k] is { } orf
                    ? $"{orf.FormKey}  {(orf.Base is { } obk && baseEditorId.TryGetValue(obk, out var oed) ? oed : "-")}"
                      + $"  at {orf.Position.X:0},{orf.Position.Y:0},{orf.Position.Z:0}  scale {orf.Scale:0.00}"
                      + (orf.Base is { } obk2 && baseMesh.TryGetValue(obk2, out var obm) ? $"  mesh {obm}" : "")
                    : groundIsNearest[k]
                      ? $"000000:TERRAIN  LandHeightmap  at {qx:0},{qy:0},{groundZ ?? 0f:0}  scale 1.00  mesh (terrain)"
                      : $"nothing within {clearReach:0} u";
                Console.WriteLine($"      {dirs[k].Name,-4} {(best[k] is { } b0 ? $"{b0,7:0.0} u" : "      -")}  {who}");
            }
        }

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

        // ---- --nav-z: what the storey filter kept, and what storeys there were to keep ---------
        //
        // The census is the flag's own POSITIVE CONTROL, and it is not optional. A band with no
        // triangles in it renders exactly like a band that works — an interior with no cyan on it —
        // and this project has twice mistaken one for the other. So the run says how many triangles
        // survived AND lists every height band the cell actually has, which turns "nothing showed
        // up" into "you asked for 1424, the floors here are at 1712 and 2000" without a second run.
        // An empty band is REFUSED outright: a picture that shows no navmesh where the author asked
        // for one storey's is a wrong answer, not a thin one.
        if (navZAt is { } navZBand)
        {
            var centroids = new List<float>();
            int navZKept = 0;
            foreach (var nav in winNav.Values)
                foreach (var tri in nav.Triangles)
                {
                    if ((tri.Flags & 0x08) != 0) continue;
                    if (tri.A < 0 || tri.B < 0 || tri.C < 0
                        || tri.A >= nav.Vertices.Count || tri.B >= nav.Vertices.Count || tri.C >= nav.Vertices.Count) continue;
                    centroids.Add((nav.Vertices[tri.A].Z + nav.Vertices[tri.B].Z + nav.Vertices[tri.C].Z) / 3f);
                    if (NavZKeeps(nav, tri)) navZKept++;
                }
            // Storeys are found rather than assumed: sort the centroids and cut wherever consecutive
            // heights are further apart than the band's own tolerance. A ramp shows up as one wide
            // band, which is honest — it IS one continuous run of ground.
            centroids.Sort();
            var bands = new List<(float Lo, float Hi, int N)>();
            foreach (var z in centroids)
            {
                if (bands.Count > 0 && z - bands[^1].Hi <= navZTol)
                { var b = bands[^1]; bands[^1] = (b.Lo, z, b.N + 1); }
                else bands.Add((z, z, 1));
            }
            string bandList = bands.Count == 0 ? "(no drawable navmesh in this cell)"
                : string.Join(", ", bands.Select(b => (b.Hi - b.Lo < 1f ? $"z {b.Lo:0}" : $"z {b.Lo:0}..{b.Hi:0}") + $" ({b.N:N0})"));
            Console.WriteLine($"   nav-z {navZBand:0} ±{navZTol:0}"
                            + (navZAuto ? $" (auto, from {(routeZ is not null ? "--route-z" : "the shot's floorZ")})" : "")
                            + $": {navZKept:N0} of {centroids.Count:N0} drawable triangle(s) kept, {centroids.Count - navZKept:N0} on other storeys");
            Console.WriteLine($"   nav-z storeys in this cell (by triangle centroid): {bandList}");
            if (navZKept == 0)
            {
                Console.Error.WriteLine($"   --nav-z REFUSED: no navmesh triangle lies within {navZTol:0} u of z {navZBand:0}, "
                                      + "so the render would show a building with no walkable ground in it — which is a wrong "
                                      + "answer, not a thin one. Pick one of the storeys listed above, or widen the tolerance "
                                      + "with --nav-z Z,TOL.");
                return 1;
            }
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

        // ---- --stations: compose the stations, write the pose file, and STOP ------------------
        // Here, because everything it needs is now in hand — the winning refs and navmeshes, the
        // cells' water, the worldspace chain, the floor filed by grid — and nothing below it is
        // wanted: the route and the render sections are what this mode is not.
        if (stationsSpec is not null)
        {
            if (stationsOut is null)
            { Console.Error.WriteLine("--stations also needs --stations-out FILE to write the poses to."); return 2; }
            if (interior)
            {
                Console.Error.WriteLine($"--stations: {cellLabel} is an INTERIOR cell. The water and "
                                      + "the floor are both looked up by exterior grid square, and an "
                                      + "interior has no grid — refusing rather than running a path "
                                      + "nothing has tested.");
                return 2;
            }
            if (ViewProbe.RouteStationMode.IsRouteSpec(stationsSpec))
                return ViewProbe.RouteStationMode.Run(new ViewProbe.RouteStationMode.Inputs
                {
                    SpecPath = stationsSpec, OutPath = stationsOut, CommandLine = replayArgs,
                    Worldspace = targetWorldspace, OrderedPaths = order.OrderedPaths,
                    BaseModel = b => baseMesh.TryGetValue(b, out var bm) ? bm : null,
                    Geometry = StationFloorGeometry, Enabled = RefEnabled, PlaneMarkerBase = planeMarkerKey,
                });
            var stationPrims = new List<ViewProbe.StationMode.Primitive>();
            foreach (var r in winRef.Values.Where(r => r.Base is { } b && b == planeMarkerKey)
                                           .OrderBy(r => r.FormKey.ToString(), StringComparer.Ordinal))
            {
                if (r.Deleted || !RefEnabled(r.FormKey)) continue;
                var pk = r.Primitive?.Type switch
                {
                    "PortalBox" => (HarnessShared.PrimitiveKind?)HarnessShared.PrimitiveKind.PortalBox,
                    "Box" => HarnessShared.PrimitiveKind.Box,
                    _ => null,
                };
                if (pk is not { } kind || r.Primitive is not { } prim) continue;
                var ph = planeHist.TryGetValue(r.FormKey, out var phh) ? phh : null;
                stationPrims.Add(new ViewProbe.StationMode.Primitive(
                    r.FormKey.ToString(), kind,
                    new System.Numerics.Vector3(prim.Bounds.X, prim.Bounds.Y, prim.Bounds.Z) * r.Scale,
                    new System.Numerics.Vector3(r.Position.X, r.Position.Y, r.Position.Z),
                    new System.Numerics.Vector3(r.Rotation.X, r.Rotation.Y, r.Rotation.Z),
                    ph?.DefinedBy ?? r.FormKey.ModKey.FileName.String,
                    cellKey.ToString()));
            }
            return ViewProbe.StationMode.Run(new ViewProbe.StationMode.Inputs
            {
                SpecPath = stationsSpec,
                OutPath = stationsOut,
                Worldspace = targetWorldspace.ToString(),
                WorldspaceEditorId = stationWorldspaceEd,
                TargetCell = cellKey.ToString(),
                Primitives = stationPrims,
                Navmeshes = winNav.Values,
                RefsByGrid = stationRefsByGrid,
                LandByGrid = stationLandByGrid,
                UnreadableLand = stationUnreadableLand,
                Reach = stationReach,
                Geometry = StationFloorGeometry,
                Enabled = RefEnabled,
                CellAt = g => cellAtGrid.TryGetValue(g, out var ci)
                    ? new HarnessShared.GridCellFacts(ci.Key.ToString(), targetWorldspace.ToString(),
                                        cellWater.TryGetValue(g, out var cw)
                                            ? new HarnessShared.DetectorModel.WaterTuple(cw.Has, false, cw.Water?.ToString(),
                                                                           cw.Height, null, null, null, null)
                                            : HarnessShared.DetectorModel.WaterTuple.None)
                    : null,
                WorldAt = name => worldChain.TryGetValue(name, out var wc)
                    ? new HarnessShared.WorldFacts(wc.Default, wc.Parent) : null,
                CommandLine = replayArgs,
            });
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
        var routeRenders = new List<ViewProbe.TexturedScene.RouteRender>();
        // (render, label, measured?, off-mesh samples, caption override) — the caption is written later, in colour.
        var routeCaptions = new List<(ViewProbe.TexturedScene.RouteRender R, string Label, bool Measured, int OffMesh, string? Caption)>();
        // ONE PLANNED ROUTE, as a function: the same gate for --route, a shot's `route`, and each planned
        // entry of its `routes[]` — resolved onto the winning navmesh, clearance-checked, refused off-mesh.
        int PlanRoute(IReadOnlyList<(float X, float Y)> xy, string label, System.Numerics.Vector4 colour, bool discs, float? arrowEvery, string? caption)
        {
            var pts = new List<System.Numerics.Vector3>(xy.Count);
            var offMesh = new List<string>();
            var tight = new List<string>();
            // --route-z beats the shot's measured floor beats eye-minus-eye-height. The last of
            // those is only sound for a camera STANDING on the route's own floor: from a top-down
            // a thousand units up it is nonsense, and it decides which storey waypoint 1 lands on.
            float refZ = routeZ ?? shotFloorZ ?? (eyePos.Value.Z - eyeHeight);
            string refWhy = routeZ is not null ? "stated: --route-z"
                          : shotFloorZ is not null ? "measured, from the shot's floorZ"
                          : $"ASSUMED: eye minus {eyeHeight:0} — pass --route-z if this camera is not standing on the route's floor";
            Console.WriteLine($"   route: {xy.Count} waypoint(s)"
                            + (label.Length > 0 ? $" — {label}" : "")
                            + $"; floor reference z {refZ:0} ({refWhy})");
            for (int wi = 0; wi < xy.Count; wi++)
            {
                var (wx, wy) = xy[wi];
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
                Console.Error.WriteLine($"   route REFUSED: {offMesh.Count} of {xy.Count} waypoint(s) are not on walkable navmesh."
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
            int sampleCount = 0, offSamples = 0, airSamples = 0;

            // ---- --route-solve: the legs are FOUND, not asserted -----------------------------
            //
            // The straight-leg walk below stays the default and stays unchanged. This is the opt-in:
            // the waypoints become endpoints, and each pair is joined by an actual walk over the
            // navmesh's own connectivity graph — pathfound, then pulled taut, then sampled onto the
            // mesh and CHECKED by the same rule as a straight leg. The check is not a formality: the
            // funnel is exact for a channel, so a solved leg that fails it means the graph and the
            // geometry disagree, and that is worth refusing over.
            if (routeSolve)
            {
                var (gnodes, grpt) = NavGraph.Build(winNav);
                // The typed links: a held ledge or toggle splits pieces and blocks a climb, so the counts say why.
                var gpol = grpt.Policy ?? NavGraph.LinkPolicy.Default;
                string walkedKinds = "Portal" + (gpol.LedgeUp ? ", Ledge Up" : "") + (gpol.LedgeDown ? ", Ledge Down" : "")
                                   + (gpol.EnableDisablePortal ? ", Enable/Disable" : "");
                var heldKinds = new List<string>();
                if (!gpol.LedgeUp && grpt.LedgeUpLinks > 0) heldKinds.Add($"{grpt.LedgeUpLinks:N0} Ledge Up");
                if (!gpol.LedgeDown && grpt.LedgeDownLinks > 0) heldKinds.Add($"{grpt.LedgeDownLinks:N0} Ledge Down");
                if (!gpol.EnableDisablePortal && grpt.EnableDisableLinks > 0) heldKinds.Add($"{grpt.EnableDisableLinks:N0} Enable/Disable");
                if (grpt.UnknownTypeLinks > 0) heldKinds.Add($"{grpt.UnknownTypeLinks:N0} of no known type");
                Console.WriteLine($"   route-solve: graph over the winning navmesh — {grpt.Triangles:N0} triangle(s), "
                                + $"{grpt.IntraLinks:N0} intra-mesh + {grpt.CrossLinks:N0} cross-mesh edge link(s), "
                                + $"{grpt.DanglingLinks:N0} dangling, {grpt.Components:N0} connected component(s); "
                                + $"{grpt.ExternalLinks:N0} cross-mesh link(s) lead OUT of the loaded cell (a boundary, not a defect) "
                                + $"and {grpt.DoorTriangles:N0} door triangle(s) are recorded and NOT traversed "
                                + "(the far side of a door is another cell, which this run never loaded); "
                                + $"ledge links {grpt.LedgeUpLinks:N0} up + {grpt.LedgeDownLinks:N0} down, {grpt.EnableDisableLinks:N0} enable/disable, "
                                + $"{grpt.UnknownTypeLinks:N0} unknown type, {grpt.HeldLinks:N0} held (not walked; this run walks {walkedKinds} links)");
                // A component count is a fact nobody can act on; an island's SIZE and WHERE it is
                // are a coordinate to probe. Printed whenever the mesh is in more than one piece,
                // because that is the thing that makes a pair unreachable.
                if (grpt.Components > 1)
                    Console.WriteLine($"   route-solve: the mesh is in {grpt.Components} piece(s) — mainland {grpt.ComponentSizes[0].Size:N0} tri, "
                                    + "island(s) " + string.Join("; ", grpt.ComponentSizes.Skip(1)
                                        .Select(c => $"{c.Size:N0} tri around {c.Where.X:0},{c.Where.Y:0},{c.Where.Z:0}")));
                // The build's own positive control. A graph with no edges finds no path between ANY
                // pair, and "not connected" is exactly what that looks like from the outside — so it
                // is caught here, where it can be named, instead of being reported as a finding
                // about the building.
                if (grpt.IntraLinks + grpt.CrossLinks == 0)
                {
                    Console.Error.WriteLine("   route-solve REFUSED: the navmesh graph came out with ZERO edges, so nothing is "
                                          + "reachable from anything and every answer it gave would be 'no path'. That is a defect "
                                          + "in the graph, not a finding about this cell"
                                          + (grpt.LinkTablesPresent ? "." : " — none of these records carry an edge-link table at all."));
                    return 1;
                }
                var unreachable = new List<string>();
                for (int li = 0; li + 1 < pts.Count; li++)
                {
                    var a = pts[li]; var b = pts[li + 1];
                    float straight = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    var s0 = NavGraph.TriangleAt(gnodes, a.X, a.Y, a.Z);
                    var g0 = NavGraph.TriangleAt(gnodes, b.X, b.Y, b.Z);
                    if (s0 is not { } sTri || g0 is not { } gTri)
                    { unreachable.Add($"leg {li + 1} (wpt {li + 1} -> {li + 2}): an endpoint is not on any drawable triangle"); legs.Add(new[] { a, b }); continue; }
                    // A*, the funnel, and the channel straightened round any interior vertex it bent at (NavGraph.TautPath).
                    var taut = NavGraph.TautPath(gnodes, sTri.Tri, gTri.Tri,
                        new System.Numerics.Vector2(a.X, a.Y), new System.Numerics.Vector2(b.X, b.Y));
                    if (taut is null)
                    { unreachable.Add($"leg {li + 1} (wpt {li + 1} -> {li + 2}, {straight:0} u apart in plan): NO walkable path over the navmesh"); legs.Add(new[] { a, b }); continue; }
                    var channel = taut.Channel;
                    var corners = taut.Corners;

                    // Sampled the same 32 u as a straight leg, but with the storey reference taken from
                    // the channel triangle each sample lies in rather than interpolated between the
                    // endpoints. A solved leg is allowed to climb or drop — that is half the point of
                    // solving it — and a chord between its ends is a poor guide to which floor its middle is on.
                    // ---- the corner inset -------------------------------------------------
                    //
                    // A taut path HUGS THE WALL — that is what taut means — so its interior corners
                    // land exactly ON the mesh boundary, and a segment between two of them runs
                    // ALONG it. At that precision "is this point on navmesh" is decided by float
                    // rounding: the first leg tried here grazed the boundary by 0.13 u and the
                    // off-mesh check, correctly by its own rule, called the path off the mesh. A
                    // knife-edge that reports a FINDING about the navmesh is worse than no check.
                    //
                    // So each interior corner is nudged a few units off the boundary, along the
                    // bisector of the two path directions leaving it. For a path bending around a
                    // single peg the bisector points into the free wedge and that is the whole
                    // story — but a funnel corner is not always a peg. Consecutive corners often
                    // lie along ONE boundary edge (that is precisely the grazing case), and there
                    // the bisector is near-perpendicular to the path and points AT the wall. So
                    // BOTH directions are offered and the MESH decides which is free, rather than
                    // a sign argument deciding it: measured here, the bisector was the wrong way
                    // at both corners of the leg that grazed, and reasoning it out got the side
                    // wrong twice before the probe settled it.
                    //
                    // VERIFIED, never assumed. A moved corner is kept only if it is itself on
                    // walkable ground at this leg's storey — which is what stops an inset from
                    // pushing a path through a thin wall in a doorway, the one way it could do
                    // harm; if neither side is walkable the original corner stands and the 32 u
                    // check downstream still gets to complain. The endpoints are never moved:
                    // those are the author's waypoints, and they belong where they were put.
                    // The inset itself lives in NavGraph.InsetCorners (moved 2026-09-25, unchanged),
                    // so the station rule's route sampler insets its paths with the same code.
                    NavGraph.InsetCorners(corners, a.Z,
                        (ix, iy, iz) => ViewProbe.BatEmit.NearestCoverZ(winNav, ix, iy, iz),
                        ViewProbe.BatEmit.SameFloorZ, gnodes, channel);
                    if (Environment.GetEnvironmentVariable("VP_SOLVE_DEBUG") == "1")
                        Console.WriteLine("      [dbg] corners: " + string.Join("  ", corners.Select(cc => $"{cc.X:0.###},{cc.Y:0.###}")));
                    // Each sample's storey is the channel triangle it lies in (NavGraph.ChannelTriangleAt): the channel is
                    // the triangle sequence an actor walks, so a drop down a walked Ledge Down lands the samples on the
                    // lower floor however tall it is. A sample in no channel triangle keeps the last height (an inset
                    // corner can sit on a same-floor neighbour). One that is also off the ground where the channel's
                    // step is a walked ledge link is IN THE AIR over the ledge's plan gap, which an actor crosses
                    // falling; any other off-ground sample is a path leaving the mesh, and is refused below.
                    var leg = new List<System.Numerics.Vector3>();
                    float refZ2 = a.Z;
                    int legOff = 0, airborne = 0, cursor = 0; float firstOffAt = 0f, walked = 0f, longestAir = 0f;
                    var firstOffXY = System.Numerics.Vector2.Zero;
                    bool InChannel(System.Numerics.Vector2 q, int from) => NavGraph.ChannelTriangleAt(gnodes, channel, q.X, q.Y, from) is not null;
                    float airStartPlan = 0f, planWalked = 0f; bool inAir = false;
                    var prevXY = new System.Numerics.Vector2(a.X, a.Y);
                    for (int ci = 0; ci + 1 < corners.Count; ci++)
                    {
                        var c0 = corners[ci]; var c1 = corners[ci + 1];
                        float segLen = System.Numerics.Vector2.Distance(c0, c1);
                        int steps = Math.Clamp((int)MathF.Ceiling(segLen / SampleEvery), 1, 512);
                        for (int si = ci == 0 ? 0 : 1; si <= steps; si++)
                        {
                            float f = (float)si / steps;
                            float x = c0.X + (c1.X - c0.X) * f, y = c0.Y + (c1.Y - c0.Y) * f;
                            var sxy = new System.Numerics.Vector2(x, y);
                            float step2 = System.Numerics.Vector2.Distance(prevXY, sxy);
                            int fromCursor = cursor;
                            var hit = NavGraph.ChannelTriangleAt(gnodes, channel, x, y, cursor);
                            if (hit is { } h) { cursor = h.Index; refZ2 = h.Z; }
                            float? gz = ViewProbe.BatEmit.NearestCoverZ(winNav, x, y, refZ2);
                            bool onFloor = gz is { } gg && MathF.Abs(gg - refZ2) <= ViewProbe.BatEmit.SameFloorZ;
                            bool air = !onFloor && hit is null && NavGraph.LedgeStep(gnodes, channel, cursor);
                            // The air run's length is measured where the path leaves and re-enters the channel, found by
                            // bisection between the two samples either side, so it is the gap, not a multiple of 32 u.
                            float Edge(System.Numerics.Vector2 inside, System.Numerics.Vector2 outside, int from)
                            {
                                float lo = 0f, hi = 1f;
                                for (int it = 0; it < 24; it++)
                                {
                                    float mid = (lo + hi) / 2f;
                                    if (InChannel(System.Numerics.Vector2.Lerp(inside, outside, mid), from)) lo = mid; else hi = mid;
                                }
                                return System.Numerics.Vector2.Distance(inside, outside) * lo;
                            }
                            if (air && !inAir) { inAir = true; airStartPlan = planWalked + Edge(prevXY, sxy, cursor); }
                            if (!air && inAir)
                            {
                                inAir = false;
                                float entry = planWalked + step2 - Edge(sxy, prevXY, cursor);
                                longestAir = MathF.Max(longestAir, entry - airStartPlan);
                            }
                            planWalked += step2; prevXY = sxy;
                            if (air) airborne++;
                            else if (!onFloor) { if (legOff == 0) { firstOffAt = walked; firstOffXY = sxy; } legOff++; }
                            else refZ2 = gz!.Value;
                            var p = new System.Numerics.Vector3(x, y, onFloor ? gz!.Value : refZ2);
                            // A step that drops over a walked ledge link is fallen, not walked: it adds its plan length only.
                            bool fell = air || inAir;
                            for (int k = fromCursor; !fell && k < cursor; k++) fell = NavGraph.LedgeStep(gnodes, channel, k);
                            if (leg.Count > 0)
                                walked += fell ? System.Numerics.Vector2.Distance(new(leg[^1].X, leg[^1].Y), sxy)
                                               : System.Numerics.Vector3.Distance(leg[^1], p);
                            leg.Add(p);
                        }
                    }
                    if (leg.Count < 2) leg = new List<System.Numerics.Vector3> { a, b };
                    sampleCount += leg.Count; offSamples += legOff; airSamples += airborne;
                    if (legOff > 0)
                        strayed.Add($"leg {li + 1} (wpt {li + 1} -> {li + 2}, SOLVED): {legOff} of {leg.Count} samples are off "
                                  + $"walkable ground, first at {firstOffAt:0} u along it, at {firstOffXY.X:0},{firstOffXY.Y:0} "
                                  + $"(probe it: --nav-at {firstOffXY.X:0},{firstOffXY.Y:0}) — the graph and the geometry disagree here");
                    // The measurement, not just the picture: how far an actor would actually have to
                    // walk, against how far apart the two ends are. A detector reporting a 1,800 u
                    // straight-line split is making a weaker claim than this number does.
                    // The z SPAN is printed so a reader sees which storeys the leg walks: "z 1424..1424"
                    // stayed on one; "0..200" dropped or climbed, and the reader gets to decide whether
                    // it was meant to. Samples in the air over a ledge gap are counted, with the longest
                    // run, so a long gap is visible without a bound having to guess one.
                    float lz0 = leg.Min(q => q.Z), lz1 = leg.Max(q => q.Z);
                    Console.WriteLine($"      leg {li + 1}: {channel.Count} triangle(s) crossed, {corners.Count} corner(s) after the funnel"
                                    + $" — {walked:0} u to walk vs {straight:0} u straight line"
                                    + (straight > 1f ? $"  (x{walked / straight:0.00})" : "")
                                    + $", z {lz0:0}..{lz1:0}"
                                    + (airborne > 0 ? $", {airborne} sample(s) in the air over a walked ledge link, longest {longestAir:0} u" : "")
                                    + (taut.Reroutes > 0 ? $", re-routed round {(taut.Reroutes == 1 ? "an interior vertex" : $"{taut.Reroutes} interior vertices")}, {taut.Saved:0} u shorter" : ""));
                    legs.Add(leg);
                }
                if (unreachable.Count > 0)
                {
                    // Refused loudly, and NOT quietly downgraded to a straight line. An unreachable
                    // pair is the interesting answer: it says these two places are not joined by
                    // walkable ground, which is the whole question a reachability finding turns on.
                    Console.Error.WriteLine($"   route REFUSED: --route-solve found no walkable path for {unreachable.Count} of "
                                          + $"{pts.Count - 1} leg(s). The navmesh has {grpt.Components:N0} connected component(s) in this cell"
                                          + (grpt.DoorTriangles > 0 ? $" and {grpt.DoorTriangles} door triangle(s) leading out of it — a route through a door is not something one cell's navmesh can answer" : "")
                                          + (grpt.HeldLinks > 0 ? $"; {grpt.HeldLinks:N0} cross-mesh link(s) are held, not walked ({string.Join(", ", heldKinds)}), so a pair only they join has no path here" : "")
                                          + ". An unreachable pair is a FINDING, not something to paper over with a straight line.");
                    foreach (var m in unreachable) Console.Error.WriteLine($"      {m}");
                    return 1;
                }
            }
            else
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
            Console.WriteLine($"   route: {legs.Count} {(routeSolve ? "SOLVED leg(s)" : "leg(s)")} sampled every {SampleEvery:0} u onto the winning navmesh "
                            + $"— {sampleCount:N0} point(s), {sampleCount - offSamples - airSamples:N0} on walkable ground"
                            + (airSamples > 0 ? $", {airSamples:N0} in the air over a walked ledge link" : ""));
            // Same rule as an off-mesh waypoint, and for the same reason: a line drawn across
            // ground the game will not let anyone walk looks exactly as authoritative as one that
            // is right. The waypoints being fine is not the claim the picture makes.
            if (strayed.Count > 0)
            {
                Console.Error.WriteLine($"   route REFUSED: {strayed.Count} leg(s) leave walkable navmesh BETWEEN their waypoints."
                                      + (routeSolve
                                         ? " These legs were SOLVED over the navmesh graph, so this is not an authoring mistake: the"
                                         + " funnel is exact for a triangle channel, and a solved path leaving the mesh means the graph's"
                                         + " links and the triangles' shapes disagree. Treat it as a finding about the navmesh."
                                         : " The waypoints are fine; the straight line between them is not — add a waypoint where the"
                                         + " walkable ground actually turns, move one, or pass --route-solve to have the path found for you."));
                foreach (var m in strayed) Console.Error.WriteLine($"      {m}");
                return 1;
            }
            var planned = new ViewProbe.TexturedScene.RouteRender(legs, pts, colour, discs, arrowEvery);
            routeRenders.Add(planned); routeCaptions.Add((planned, label, false, 0, caption));
            return 0;
        }
        if (shotRoute.Count > 1 && PlanRoute(shotRoute, routeLabel, routeColor, routeDiscs, routeArrowEvery, null) != 0) return 1;

        // ---- the shot's own routes[]: planned ones through the gate above, measured ones drawn as sampled --
        if (shotSpec?.Routes is { Count: > 0 } wlRoutes)
        {
            for (int ri = 0; ri < wlRoutes.Count; ri++)
            {
                var wr = wlRoutes[ri];
                string rlabel = wr.Label ?? $"route {ri + 1}";
                if (!TryRouteColour(wr.Color, out var rcol, out var rcErr)) { Console.Error.WriteLine($"worklist route '{rlabel}': {rcErr}"); return 1; }
                float? rArrows = routeArrowEvery;
                if (wr.Arrows is { } asp)
                    rArrows = asp == "auto" ? null : asp == "end" ? 0f : float.Parse(asp);   // validated on read
                bool measured = wr.Points[0].Count == 3;
                if (!measured)
                {
                    if (PlanRoute(wr.Points.Select(pt => (pt[0], pt[1])).ToList(), rlabel, rcol, wr.Discs ?? routeDiscs, rArrows, wr.Caption) != 0) return 1;
                    continue;
                }
                // MEASURED: the engine's own samples. z is the engine's, the line runs exactly through the
                // points, and the navmesh is consulted only to REPORT — a sample it does not cover is a
                // finding about the walk, never a reason to refuse the picture. Straight in 3D between
                // samples: the samples are the measurement's own resolution, and a mesh-following leg
                // between them would be the renderer editing the evidence.
                var mpts = wr.Points.Select(pt => new System.Numerics.Vector3(pt[0], pt[1], pt[2])).ToList();
                int off = 0;
                foreach (var mp in mpts)
                    if (!ViewProbe.BatEmit.CoverHeights(winNav, mp.X, mp.Y).Any(h => MathF.Abs(h - mp.Z) <= 96f)) off++;
                float mwalk = 0f;
                for (int mi = 0; mi + 1 < mpts.Count; mi++) mwalk += System.Numerics.Vector3.Distance(mpts[mi], mpts[mi + 1]);
                var measuredRender = new ViewProbe.TexturedScene.RouteRender(
                    new IReadOnlyList<System.Numerics.Vector3>[] { mpts }, mpts, rcol, wr.Discs ?? false, rArrows);
                routeRenders.Add(measuredRender); routeCaptions.Add((measuredRender, rlabel, true, off, wr.Caption));
                Console.WriteLine($"   path (measured) {rlabel}: {mpts.Count} sample(s), {mwalk:0} u, z as sampled"
                                + (off > 0 ? $"; {off} sample(s) over no navmesh within 96 u — reported, not refused"
                                           : "; every sample over navmesh within 96 u"));
            }
        }

        var evaluatedNav = navSelection.Substitute(winNav, navByPlugin);
        var (navTris, redTris) = BuildNavSets(evaluatedNav);
        IReadOnlyDictionary<FormKey, NavmeshGeometry> AsIfWon(string plugin)
        {
            var result = new Dictionary<FormKey, NavmeshGeometry>(winNav);
            if (navByPlugin.TryGetValue(plugin, out var own)) foreach (var kv in own) result[kv.Key] = kv.Value;
            return result;
        }
        var displayNav = navPlugin is not null
            ? navByPlugin.GetValueOrDefault(navPlugin) ?? new Dictionary<FormKey, NavmeshGeometry>() : evaluatedNav;
        string navLayerLabel = navPlugin ?? (navVersionSpecs.Count > 0 ? "winning navmesh with explicit record substitutions" : "winning state");

        if (redTris.Count > 0)
        {
            var cen = System.Numerics.Vector3.Zero;
            foreach (var (a, b, c) in redTris) cen += (a + b + c) / 3f;
            cen /= redTris.Count;
            Console.WriteLine($"   red navmesh centroid: {cen.X:0},{cen.Y:0},{cen.Z:0}  ({redTris.Count} triangles)");
            Console.WriteLine($"   suggested view:  --eye {cen.X:0},{cen.Y - 450:0},{cen.Z + 260:0} --look {cen.X:0},{cen.Y:0},{cen.Z:0}");
        }

        var modFolders = assetMods.Select(m => Path.Combine(modsDir, m)).ToList();

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
                    // Refused, not assumed non-light (PluginRead.cs): a wrong guess shifts every runtime FormID caption.
                    catch (Exception ex) { throw ViewProbe.PluginRead.Failed(pp, order.OrderedPaths.ToList().IndexOf(pp), order.OrderedPaths.Count, "the runtime FormID header read", ex); }
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
        // Same rule for the storey filter: navmesh absent from four fifths of a temple floor plan
        // is a finding if the picture does not say it was filtered to one storey.
        string CullCaption() => (cullAbove is { } cca ? $" — SLICED above z {cca:0}" : "")
                              + (navZAt is { } nzc ? $" — NAVMESH z {nzc:0} ±{navZTol:0} ONLY" : "");

        string? DoorCaption()
            => conflictDoors.Count == 0 ? null
             : "ORANGE door " + string.Join("   ", conflictDoors.Select(fk => $"{fk} = FormID {RuntimeId(fk)}"));

        string RedCaption(IReadOnlyDictionary<FormKey, NavmeshGeometry> navs)
            => string.Join("   ", redNav.Keys.Select(fk =>
                   $"RED {fk} = FormID {RuntimeId(fk)}" +
                   (navs.TryGetValue(fk, out var nv) ? $" ({nv.Triangles.Count} tris)" : " (not provided)")));

        // ---- occlusion planes: every PlaneMarker ref the winning state places in the cell (+ ring) --
        // Built whether or not the layer is drawn: the occlusion preview and the report read the same
        // list, and the count line prints either way — a plane the scene holds and says nothing about
        // is the silent category this renderer refuses to have. Sorted by FormKey so the order, the
        // colours and the PNG are the same for the same inputs.
        var planes = new List<ViewProbe.PlaneRender>();
        string planeLegend = "";
        {
            int plDisabledParent = 0, plDisabledFlag = 0, plDeleted = 0, plNoPrim = 0;
            var noPrimKinds = new Dictionary<string, int>();
            foreach (var r in winRef.Values.Where(r => r.Base is { } b && b == planeMarkerKey)
                                           .OrderBy(r => r.FormKey.ToString(), StringComparer.Ordinal))
            {
                if (r.Deleted) { plDeleted++; continue; }
                if (!RefEnabled(r.FormKey))
                { if (refEnableParent.ContainsKey(r.FormKey)) plDisabledParent++; else plDisabledFlag++; continue; }
                ViewProbe.PlaneKind? kind = r.Primitive?.Type switch
                {
                    "PortalBox" => ViewProbe.PlaneKind.PortalBox,
                    "Box" => ViewProbe.PlaneKind.Box,
                    _ => null,
                };
                if (kind is null || r.Primitive is not { } prim)
                {
                    plNoPrim++;
                    string k = r.Primitive?.Type ?? "(no XPRM)";
                    noPrimKinds[k] = noPrimKinds.TryGetValue(k, out var nk) ? nk + 1 : 1;
                    continue;
                }
                // Every real ref has a history from the walk; only a synthetic prop would not, and a
                // prop cannot be a plane. Classified against THIS cell's contested state.
                var h = planeHist.TryGetValue(r.FormKey, out var ph) ? ph : null;
                string definedBy = h?.DefinedBy ?? r.FormKey.ModKey.FileName.String;
                var prov = h?.Classify(cellContestedBy.Count > 0) ?? ViewProbe.PlaneProvenance.Added;
                planes.Add(new ViewProbe.PlaneRender(r.FormKey, kind.Value,
                    new System.Numerics.Vector3(prim.Bounds.X, prim.Bounds.Y, prim.Bounds.Z),
                    new System.Numerics.Vector3(r.Position.X, r.Position.Y, r.Position.Z),
                    r.Rotation, r.Scale, prov, definedBy, h?.ChangedBy,
                    h?.Chain ?? new List<string> { definedBy }, RuntimeId(r.FormKey)));
            }
            int nPortal = planes.Count(p => p.Kind == ViewProbe.PlaneKind.PortalBox), nBox = planes.Count - nPortal;
            int Prov(ViewProbe.PlaneProvenance pv) => planes.Count(p => p.Provenance == pv);
            int nVan = Prov(ViewProbe.PlaneProvenance.Vanilla), nInh = Prov(ViewProbe.PlaneProvenance.Inherited);
            int nAdd = Prov(ViewProbe.PlaneProvenance.Added), nMov = Prov(ViewProbe.PlaneProvenance.Moved);
            string By(ViewProbe.PlaneProvenance pv, Func<ViewProbe.PlaneRender, string?> who)
                => string.Join(", ", planes.Where(p => p.Provenance == pv).GroupBy(p => who(p) ?? "?")
                                           .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
            int plDropped = plDisabledParent + plDisabledFlag + plDeleted + plNoPrim;
            if (planes.Count + plDropped > 0)
            {
                Console.WriteLine($"   occlusion planes: {planes.Count} in the cell{(interior || !ringRefs ? "" : " + ring")}"
                                + $" — {nPortal} PortalBox, {nBox} Box; {nVan} vanilla, {nInh} inherited, {nAdd} added, {nMov} moved");
                if (nAdd > 0) Console.WriteLine($"      added by: {By(ViewProbe.PlaneProvenance.Added, p => p.DefinedBy)}");
                if (nMov > 0) Console.WriteLine($"      moved by: {By(ViewProbe.PlaneProvenance.Moved, p => p.ChangedBy)}");
                Console.WriteLine(cellContestedBy.Count > 0
                    ? $"      cell contested by: {string.Join(", ", cellContestedBy)}"
                    : "      cell contested by: nobody — no plugin beyond the base masters places refs in it, so unchanged vanilla planes read as vanilla, not inherited");
                if (plDropped > 0)
                    Console.WriteLine($"      not drawn: {plDisabledParent} disabled via enable parent, {plDisabledFlag} initially disabled (own flag), "
                                    + $"{plDeleted} deleted, {plNoPrim} without a Box/PortalBox primitive"
                                    + (noPrimKinds.Count > 0 ? $" ({string.Join(", ", noPrimKinds.Select(kv => $"{kv.Key} {kv.Value}"))})" : ""));
                if (!showPlanes && occlusionMode == "off" && !planesReport)
                    Console.WriteLine("      (--planes draws them; --occlusion on|diff previews their culling; --planes-report lists them)");
                planeLegend = $"PLANES {planes.Count}: grey vanilla {nVan} · gold inherited {nInh} · magenta added {nAdd} · teal moved {nMov}"
                            + (nBox > 0 ? $" — {nBox} Box kind drawn fainter as its box" : "")
                            + " — outline dashed where hidden";
            }
            else if (showPlanes || occlusionMode != "off" || planesReport)
                Console.WriteLine("   occlusion planes: none in this cell" + (interior || !ringRefs ? "" : " or its ring"));
        }


        // ---- --npc: pin each spec to (NPC base record, world placement) -------------------
        // The spec FormKey may be the placed ACHR (a console click gives the ref) or the NPC_
        // base; the load walk watched for both. Explicit @X,Y,Z is the "should be HERE" case
        // and overrides the ref's own placement; explicit heading is degrees, `getangle z`
        // convention. No placement anywhere and no @position is an error, not a guess.
        var npcPlacements = new List<(FormKey NpcKey, ViewProbe.NpcPlacement Placement, string LabelHint)>();
        foreach (var (key, pos, headingDeg, label, raw) in shotNpcParsed)
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
        using var graphicsContextRed = graphicsCell is null ? graphicsReuseRed?.BeginContext() : null;
        using var fpGwOwner = graphicsCell is null ? new GameWindow(GameWindowSettings.Default, fpNws) : null;
        var fpGw = graphicsCell?.Window ?? fpGwOwner!;
        if (graphicsCell is null)
        {
            graphicsContextRed?.BindCreatedContext(fpGw.Context);
            fpGw.MakeCurrent();
            graphicsContextRed?.MadeCurrent();
        }
        else graphicsCell.MakeCurrent();

        // ---- --npc: build the actors on this context --------------------------------------
        // Record side first (a full-order link cache over fresh overlays — the walk above
        // disposed its own), then the runtime outfit stack, then one VM per NPC through the
        // character pipeline, world-baked. The VMs (and these overlays) must outlive every
        // panel of the shot: the shot's owner-release scope (npcOwners) runs NpcCleanup() once on every exit,
        // a fault included.
        var npcActors = new List<ViewProbe.NpcActorRender>();
        ViewProbe.NpcActorBuilder.Host? npcHost = null;
        var npcLcMods = new List<ISkyrimModGetter>();
        // Shot-scoped owners are released from the shot's cleanup scope, never at the normal returns alone
        // (Gate B6 review, Q4): a fault after the actors are built must not leave their VMs alive in a retained context.
        using var npcOwners = new ViewProbe.TexturedScene.ShotOwnerRelease(NpcCleanup);
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
            int npcIndex = -1;
            foreach (var pp2 in order.OrderedPaths)
            {
                // Refused, not skipped (PluginRead.cs): a skipped plugin resolved NPCs from an earlier plugin's records.
                npcIndex++;
                ViewProbe.RenderStages.Tick?.Invoke();
                npcLcMods.Add(ViewProbe.PluginRead.Open(pp2, npcIndex, order.OrderedPaths.Count, "the NPC link cache"));
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
                List<SkyRender.Character.MeshOverride>? overrides = null;
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
        foreach (var (rr, rlab, measured, off, capOverride) in routeCaptions)
        {
            if (capOverride is "") continue;                     // the author asked for no line
            // Distance WALKED, summed along the ground-following legs — a chord between waypoints
            // would under-report every slope and staircase on the path.
            float walk = 0f;
            foreach (var leg in rr.Legs)
                for (int wi = 0; wi + 1 < leg.Count; wi++) walk += System.Numerics.Vector3.Distance(leg[wi], leg[wi + 1]);
            var rc3 = new System.Numerics.Vector3(rr.Color.X, rr.Color.Y, rr.Color.Z);
            if (capOverride is { } capText) { colourCaptions.Add((capText, rc3)); continue; }
            colourCaptions.Add((measured
                ? $"PATH {rlab} — {rr.Waypoints.Count} samples, {walk:0} u — measured: z as the engine reported it"
                  + (off > 0 ? $", {off} sample(s) over no navmesh" : "")
                : $"ROUTE {rr.Waypoints.Count} waypoints, {walk:0} u"
                  + (rlab.Length > 0 ? $" — {rlab}" : "")
                  + " — z from the winning navmesh, every waypoint clearance-checked and every leg sampled onto it",
                rc3));
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
            // G3 RED (test-only): each borrowed mesh's handles, liveness and renderer membership just before and
            // just after its owning VMs are disposed. Inert without a RED session.
            var redBorrowed = graphicsReuseRed is null ? null : npcActors.SelectMany(a => a.Meshes).ToArray();
            var redSnapshot = graphicsReuseRed?.BorrowedOwnersDisposing(redBorrowed!, graphicsCell?.Renderer);
            npcHost?.Disposer();
            if (redSnapshot is not null) graphicsReuseRed?.BorrowedOwnersDisposed(redSnapshot, graphicsCell?.Renderer);
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
        // args = the command that reproduces THIS frame — the process's own for a single-shot run,
        // where it is already byte-exact, and this shot's own camera for a worklist shot, which
        // otherwise inherited the worklist's command and could not be replayed at all once the
        // worklist file was gone (ReplayCommand.cs). pose = the camera those args resolved to THIS
        // run (matters when the camera came from --eye-ref/--look-ref, whose positions move with
        // the load order).
        string shotReplay = replayCam is { } shotCam && worklist is not null
            ? ViewProbe.ReplayCommand.ForShot(args, worklist.Cells[wlCell].Cell, shotCam,
                  new ViewProbe.ReplayCommand.ShotView(navMode, cullAbove, showPlanes,
                                                       occlusionMode, stateSpec, canvasAspect),
                  ViewProbe.ReplayCommand.Requires(shotSpec), worklistPaths.Count == 1 ? worklistPaths[0] : null,
                  shotPlan?.ReplayWorklistPath)
            : replayArgs;
        if (collectArtifacts) completedArtifacts.SetReplay(shotReplay);
        string camMeta = $"args: {shotReplay} | pose: {camPose}"
                       + $" look {lookPos.Value.X:0.##},{lookPos.Value.Y:0.##},{lookPos.Value.Z:0.##}"
                       + $" | cell {cellLabel}";
        if (navCellSpecs.Count > 0 || navVersionSpecs.Count > 0)
            camMeta += " | navmesh cells " + string.Join(",", navSelection.Cells.Keys)
                     + " | navmesh layer " + navLayerLabel;

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
        string shotOutDir = outDir;
        if (shotPlan is { Source: not null })
        {
            safe = shotPlan.OutputStem!;
            shotOutDir = Path.GetFullPath(Path.Combine(outDir, shotPlan.ScratchDirectory!));
            string root = Path.GetFullPath(outDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
            if (!shotOutDir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("composite scratch directory escapes --out");
            Directory.CreateDirectory(shotOutDir);
        }
        if (collectArtifacts)
        {
            // Refuse every deterministic path before the first writer runs. In particular, an
            // early configuration sidecar must not overwrite old evidence and only later discover
            // that the frame/report destination was already occupied.
            var renderFiles = compare is { } reservedCompare
                ? new[] { reservedCompare.A, reservedCompare.B, "winning state" }
                    .Select((label, index) => Path.Combine(shotOutDir,
                        $"{safe}-cmp{index + 1}-{new string(label.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray())}.png"))
                    .ToList()
                : new List<string> { Path.Combine(shotOutDir, $"{safe}-fp.png") };
            var plannedFiles = new List<string>(renderFiles);
            var plannedDirectories = new List<string>();
            if (compare is not null) plannedFiles.Add(Path.Combine(shotOutDir, $"{safe}-compare.png"));
            if (reportedConfiguration is not null) plannedFiles.Add(Path.Combine(shotOutDir, $"{safe}-configuration.json"));
            if (navCellSpecs.Count > 0 || navVersionSpecs.Count > 0)
                plannedFiles.Add(Path.Combine(shotOutDir, $"{safe}-navmeshes.json"));
            if (planesReport)
                foreach (string frame in renderFiles)
                {
                    string stem = Path.GetFileNameWithoutExtension(frame);
                    plannedFiles.Add(Path.Combine(shotOutDir, stem + "-planes.json"));
                    string evidence = Path.Combine(shotOutDir, stem + "-audit-evidence");
                    plannedDirectories.Add(evidence);
                }
            ViewProbe.BatchProgress.RefuseExistingOutputs(plannedFiles, plannedDirectories);
        }

        void WriteShotText(string path, string text)
        {
            if (!collectArtifacts) { File.WriteAllText(path, text); return; }
            ViewProbe.BatchProgress.WriteTextCreateNew(path, text);
        }

        if (reportedConfiguration is not null)
        {
            // The source identity describes records/assets before diagnostic view transformations.
            // A historical navmesh substitution or cutaway must remain distinguishable from that source.
            var identity = reportedConfiguration.Identity("load-time", stateSpec ?? "baseline");
            var captureInput = new
            {
                Configuration = identity,
                CandidateSha256 = candidateSha256,
                Cell = cellKey.ToString(),
                View = new { navPlugin, navCellSpecs, navVersionSpecs, comparePlugins = compare is { } pair ? new[] { pair.A, pair.B } : null,
                             cullAbove, bosMode, stateView, replayArgs = shotReplay },
                Note = "Input configuration before diagnostic view transformations; inspect PNG pose and view settings before media qualification."
            };
            string configurationPath = Path.Combine(shotOutDir, $"{safe}-configuration.json");
            WriteShotText(configurationPath,
                System.Text.Json.JsonSerializer.Serialize(captureInput, HarnessShared.CandidateFile.Json));
            if (collectArtifacts) completedArtifacts.AddClosed(configurationPath, "sidecar");
            Console.WriteLine($"   capture input configuration: {identity.Fingerprint}");
        }

        if (navCellSpecs.Count > 0 || navVersionSpecs.Count > 0)
        {
            object DescribeLayer(string label, IReadOnlyDictionary<FormKey, NavmeshGeometry> navs) => new
            {
                Label = label,
                Records = navs.Values.OrderBy(n => n.FormKey.ToString(), StringComparer.Ordinal).Select(n => new
                {
                    FormKey = n.FormKey.ToString(), Cell = navSelection.Provenance[n].Cell.ToString(),
                    Provider = navSelection.Provenance[n].Plugin, TriangleCount = n.Triangles.Count,
                    Colour = navSelection.Cells[navSelection.Provenance[n].Cell] ?? navColor,
                    DrawableTrianglesInBand = Enumerable.Range(0,n.Triangles.Count).Count(i =>
                        ViewProbe.NavmeshSelection.IsDrawable(n,i) && NavZKeeps(n,n.Triangles[i])),
                    RedWholeRecord = redNav.TryGetValue(n.FormKey,out var marked) && marked is null,
                    RedRequests = redNav.TryGetValue(n.FormKey,out var requested) && requested is not null
                        ? requested.Order().Select(i => new { Index = i, Exists = i >= 0 && i < n.Triangles.Count,
                            DrawableInBand = ViewProbe.NavmeshSelection.IsDrawable(n,i) && NavZKeeps(n,n.Triangles[i]) }).ToArray() : null
                }).ToArray()
            };
            object[] layers = compare is { } pair
                ? new[] { DescribeLayer(pair.A,AsIfWon(pair.A)), DescribeLayer(pair.B,AsIfWon(pair.B)), DescribeLayer("winning state",winNav) }
                : new[] { DescribeLayer(navLayerLabel,displayNav) };
            var receipt = new
            {
                SchemaVersion = 1, Configuration = reportedConfiguration?.Identity("load-time",stateSpec ?? "baseline"),
                CandidateSha256 = candidateSha256, TargetCell = cellKey.ToString(),
                AdditionalCells = navCellSpecs, RecordSubstitutions = navVersionSpecs,
                Pose = new { Eye = new[] { eyePos.Value.X,eyePos.Value.Y,eyePos.Value.Z },
                    Look = new[] { lookPos.Value.X,lookPos.Value.Y,lookPos.Value.Z }, Fov = fov, Size = size },
                ReplayArgs = shotReplay, Layers = layers,
                Note = "Navmesh annotation selection only; world geometry/assets retain the evaluated configuration. Triangle admission is not pixel visibility or NPC movement evidence."
            };
            string navmeshPath = Path.Combine(shotOutDir,$"{safe}-navmeshes.json");
            WriteShotText(navmeshPath,
                System.Text.Json.JsonSerializer.Serialize(receipt,HarnessShared.CandidateFile.Json));
            if (collectArtifacts) completedArtifacts.AddClosed(navmeshPath, "navmesh");
        }

        // The target cell's own footprint, for the outline. Exteriors only — an interior has no grid
        // and no neighbours to be confused with.
        (float X0, float Y0, float X1, float Y1)? cellBounds = null;
        if (cellOutline && !interior && targetGrid is { } tgb)
            cellBounds = (tgb.X * 4096f, tgb.Y * 4096f, tgb.X * 4096f + 4096f, tgb.Y * 4096f + 4096f);

        // ---- SEAMS (--seams): every ring-cell edge whose shared LAND vertices disagree ------------
        // The engine draws each cell's LAND on its own; a shared-edge vertex the two records disagree about is a
        // step or a gap. Same decode and the same comparison as seamprobe (DetectorModel.EdgeMismatch); this is the
        // picture of it: a ribbon along each mismatched run, lifted 6 u over the higher side, yellow under 32 u,
        // orange under 128 u, red beyond. Winners only — provenance (Introduced / Inherited) is the probe's.
        var overlayLines = new List<ViewProbe.TexturedScene.OverlayLine>();
        if (drawSeams && !interior && landHeights.Count > 0)
        {
            int edges = 0, runs = 0; float worst = 0f;
            foreach (var ((gx, gy), hA) in landHeights.OrderBy(k => k.Key.Y).ThenBy(k => k.Key.X))
            {
                foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
                {
                    if (!landHeights.TryGetValue((gx + dx, gy + dy), out var hB)) continue;
                    var ea = HousecarlCore.LandHeight.Edge(hA, dx == 1 ? HousecarlCore.CellEdge.East : HousecarlCore.CellEdge.North);
                    var eb = HousecarlCore.LandHeight.Edge(hB, dx == 1 ? HousecarlCore.CellEdge.West : HousecarlCore.CellEdge.South);
                    var (mx, over) = HarnessShared.DetectorModel.EdgeMismatch(ea, eb, seamEps);
                    if (over == 0) continue;
                    edges++; worst = MathF.Max(worst, mx);
                    // Runs of consecutive vertices over eps, each its own ribbon coloured by its own worst step.
                    int i = 0;
                    while (i < 33)
                    {
                        if (MathF.Abs(ea[i] - eb[i]) <= seamEps) { i++; continue; }
                        int j = i; float runMax = 0f;
                        while (j < 33 && MathF.Abs(ea[j] - eb[j]) > seamEps) { runMax = MathF.Max(runMax, MathF.Abs(ea[j] - eb[j])); j++; }
                        var pts = new List<System.Numerics.Vector3>();
                        for (int k = Math.Max(0, i - 1); k <= Math.Min(32, j); k++)
                        {
                            float z = MathF.Max(ea[k], eb[k]) + 6f;
                            pts.Add(dx == 1 ? new System.Numerics.Vector3((gx + 1) * 4096f, gy * 4096f + k * 128f, z)
                                            : new System.Numerics.Vector3(gx * 4096f + k * 128f, (gy + 1) * 4096f, z));
                        }
                        var colour = runMax < 32f ? new System.Numerics.Vector3(1f, 0.9f, 0.2f)
                                   : runMax < 128f ? new System.Numerics.Vector3(1f, 0.55f, 0.1f) : new System.Numerics.Vector3(1f, 0.15f, 0.1f);
                        overlayLines.Add(new ViewProbe.TexturedScene.OverlayLine(pts, colour, runMax < 32f ? 1.4f : runMax < 128f ? 2f : 2.8f));
                        runs++;
                        i = j;
                    }
                    Console.WriteLine($"   seam: ({gx},{gy}) {(dx == 1 ? "E" : "N")} ({gx + dx},{gy + dy}): max Δ {mx:0} u over {over} of 33 vertices");
                }
            }
            Console.WriteLine(edges == 0
                ? $"   seams: none — every shared edge in the ring agrees within {seamEps:0.#} u"
                : $"   seams: {edges} mismatched edge(s) in the ring at eps {seamEps:0.#} u, {runs} run(s) drawn, worst {worst:0} u (yellow < 32, orange < 128, red beyond)");
        }

        // --lod: the worldspace's LOD water type (WRLD NAM3) for the terrain LOD blocks' WATER quads
        // (DistantLodLayer.cs). The quads carry their own heights; only the material comes from here.
        if (ViewProbe.DistantLodLayer.Requested && !interior)
        {
            ViewProbe.DistantLodLayer.LodWater = null;
            if (!drawWater) ViewProbe.DistantLodLayer.LodWaterWhy = "--no-water";
            else if (ViewProbe.DistantLodLayer.LodWaterKey(out var lwWhy) is not { } lwk) ViewProbe.DistantLodLayer.LodWaterWhy = lwWhy;
            else if (!watrRec.TryGetValue(lwk, out var lw)) ViewProbe.DistantLodLayer.LodWaterWhy = $"WATR {lwk} not found in the load order";
            else
                ViewProbe.DistantLodLayer.LodWater = new ViewProbe.WaterPlane(
                    0f, 0f, 0f, 0f, 0f,
                    lw.Noise is { Length: > 0 } lwn ? NormalizeTexPath(lwn) : null, lw.NoiseUv, lw.Shallow, lw.Deep, lw.Reflect,
                    lw.Reflectivity, Math.Clamp(lw.Opacity / 100f, 0f, 1f), lw.Fresnel, lw.Ed,
                    lw.FogNear, lw.FogFar, lw.FogAmount, lw.NoiseFalloff, lw.Spec);
        }
        // ---- WATER: one surface per loaded cell that has any -----------------------------------
        // Exteriors only. An interior's water is a placed basin with no grid footprint to span, so
        // there is nothing honest to draw a plane across; say so rather than inventing a rectangle.
        var waterPlanes = new List<ViewProbe.WaterPlane>();
        if (drawWater && !interior && targetGrid is { } wg)
        {
            // --water-arm: the winner's tuples (the default) or the truth's — the last Bethesda/USSEP statement.
            var armWater = waterArm == "truth" ? cellWaterTruth : cellWater;
            var wref = waterArm == "truth" ? wsWaterTruth : wsWater;
            float wsDefault = waterArm == "truth" ? wsDefaultWaterHeightTruth : wsDefaultWaterHeight;
            // The loaded cells in the order the ±1 loops have always walked them (dx outer, or dy outer); --ring's set
            // (which always holds the 3x3) adds its other cells after them, sorted the same way.
            IEnumerable<(int X, int Y)> LoadedCells(bool dxOuter)
            {
                for (int a = -1; a <= 1; a++)
                    for (int b = -1; b <= 1; b++)
                        yield return dxOuter ? (wg.X + a, wg.Y + b) : (wg.X + b, wg.Y + a);
                if (visibleSet is null) yield break;
                var rest = visibleSet.Where(c => Math.Abs(c.X - wg.X) > 1 || Math.Abs(c.Y - wg.Y) > 1);
                foreach (var c in dxOuter ? rest.OrderBy(c => c.X).ThenBy(c => c.Y) : rest.OrderBy(c => c.Y).ThenBy(c => c.X))
                    yield return c;
            }
            foreach (var key in LoadedCells(true))
                {
                    if (!armWater.TryGetValue(key, out var cw) || !cw.Has) continue;
                    // The engine's own precedence: the cell's height when it states one, the
                    // worldspace default when it holds the sentinel.
                    // The engine's reading of the sentinels, measured in game 2026-09-04 (DetectorModel.SurfaceHeight):
                    // +3.4028235E+38 and the wrapped 4.29e9 both defer to the worldspace default (Winterhold's swimmer sits
                    // at the default's depth); the negative sentinels (−2.1e9, −3.4e38) place no surface at all.
                    float? zr = HarnessShared.DetectorModel.SurfaceHeight(cw.Height, RealHeight(wsDefault) ? wsDefault : null);
                    if (zr is null && !float.TryParse(Environment.GetEnvironmentVariable("VP_WATER_Z"), out _)) continue;
                    float z = zr ?? float.NaN;
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
                             new System.Numerics.Vector3(0.30f, 0.30f, 0.30f), 0.4f, (byte)50, 0.07f,
                             0f, 0f, 0f, 0f, System.Numerics.Vector3.Zero);
                    string? noise = w.Item2 is { Length: > 0 } np ? NormalizeTexPath(np) : null;
                    waterPlanes.Add(new ViewProbe.WaterPlane(
                        z, key.Item1 * 4096f, key.Item2 * 4096f, key.Item1 * 4096f + 4096f, key.Item2 * 4096f + 4096f,
                        noise, w.Item3, w.Item4, w.Item5, w.Item6, w.Item7,
                        // WATR.Opacity is a 0..100 percentage in the CK, not a 0..255 byte.
                        Math.Clamp(w.Item8 / 100f, 0f, 1f), w.Item9, w.Item1,
                        w.Item10, w.Item11, w.Item12, w.Item13, w.Item14));
                }
            // The two arms side by side, per ring cell, with the share of the cell's LAND vertices under each surface —
            // waterprobe's visible-share number, so a surface that is drawn but under the ground is called out here.
            {
                bool differ = false;
                var lines = new List<string>();
                foreach (var k in LoadedCells(false))
                    {
                        cellWater.TryGetValue(k, out var win); cellWaterTruth.TryGetValue(k, out var tru);
                        string Arm((bool Has, float Height, FormKey? Water) t, FormKey? wsw2, float wsd)
                        {
                            if (!t.Has) return "no water";
                            float zz = HarnessShared.DetectorModel.SurfaceHeight(t.Height, RealHeight(wsd) ? wsd : null) ?? float.NaN;
                            var wk2 = t.Water ?? wsw2;
                            string vis = landHeights.TryGetValue(k, out var hh) && RealHeight(zz) ? $" vis {HarnessShared.CellBoundaryIndex.VisibleShare(hh, zz):P0}" : "";
                            return $"{(wk2 is { } wk3 && watrRec.TryGetValue(wk3, out var wr2) ? wr2.Item1 : wk2?.ToString() ?? "(no WATR)")} z {(RealHeight(zz) ? zz.ToString("0") : "no surface")}{vis}";
                        }
                        string a = Arm(win, wsWater, wsDefaultWaterHeight), b = Arm(tru, wsWaterTruth, wsDefaultWaterHeightTruth);
                        if (a != b) differ = true;
                        lines.Add($"      cell ({k.Item1},{k.Item2}): winner {a} | truth {b}{(a != b ? "   <- differs" : "")}");
                    }
                Console.WriteLine($"   water arm: {waterArm}" + (differ ? " — the winner and truth arms differ on at least one ring cell (a water mod's retype counts as a difference here; waterprobe tells them apart):" : " — the winner and truth arms agree on every ring cell"));
                if (differ || waterArm == "truth") foreach (var l in lines) Console.WriteLine(l);
            }
            if (waterPlanes.Count == 0 && armWater.Values.Any(v => v.Has))
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
                foreach (var k in LoadedCells(true))
                    {
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
            // The single-pass path's two display settings, as the engine reads them
            // (fProjectedUVDiffuseNormalTilingScale:Display, bEnableProjecteUVDiffuseNormals:Display,
            // spelled as the engine spells it). Absent keys take the engine defaults (0.5, on).
            static string? PrefsValue(string dir, string key)
            {
                var ini = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, "*.ini").FirstOrDefault(f =>
                        string.Equals(Path.GetFileName(f), "SkyrimPrefs.ini", StringComparison.OrdinalIgnoreCase))
                    : null;
                if (ini is null) return null;
                foreach (var line in File.ReadLines(ini))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                        return line[(eq + 1)..].Trim();
                }
                return null;
            }
            float projTiling = float.TryParse(PrefsValue(profileDir, "fProjectedUVDiffuseNormalTilingScale"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pt) ? pt : 0.5f;
            bool projDiffuse = PrefsValue(profileDir, "bEnableProjecteUVDiffuseNormals") is not { } pdv || pdv != "0";
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
                    mt.Model, mt.Ed, mt.SinglePass, mt.NoiseUv, projTiling, projDiffuse);
            }
            if (coveringNoMato > 0)
                Console.WriteLine($"   coverings: {coveringNoMato} base(s) name a MaterialObject no active plugin defines");
        }

        // Complete the owned walk value after the separate terrain/water walk. The raw placement
        // snapshot above was taken before state/BOS/population projection; only these follow-up
        // record facts and derived terrain arrays are added here. BOS's index/layer remain the
        // established process-owned readers and are freshly applied to each cloned working set.
        if (builtWalkCore is not null)
        {
            var factsProbe = new ViewProbe.CellFactsProbe(
                cellLabel,
                refCellOf.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal)
                         .Select(x => new KeyValuePair<FormKey, FormKey>(x.Key, x.Value)).ToArray(),
                navVersionPlugins.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                cellAtGrid.OrderBy(x => x.Key.X).ThenBy(x => x.Key.Y)
                          .Select(x => new ViewProbe.GridCellFact(x.Key.X, x.Key.Y, x.Value.Key, x.Value.Ed)).ToArray(),
                cellWaterTruth.OrderBy(x => x.Key.X).ThenBy(x => x.Key.Y)
                              .Select(x => new ViewProbe.WaterTruthFact(x.Key.X, x.Key.Y, x.Value.Has, x.Value.Height, x.Value.Water)).ToArray(),
                wsWaterTruth, wsDefaultWaterHeightTruth, stationWorldspaceEd,
                baseType.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal).ToArray(),
                baseObnd.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal)
                        .Select(x => new KeyValuePair<FormKey, ViewProbe.CellBoundsFact>(
                            x.Key, new(x.Value.Min, x.Value.Max))).ToArray(),
                ViewProbe.CellFactsRed.HashWatchSelection(shotNpcWatch, doorWatchKeys, navVersionPlugins));
            builtWalkCore.CellWater = new(cellWater);
            builtWalkCore.WorldWater = wsWater;
            builtWalkCore.WorldDefaultWaterHeight = wsDefaultWaterHeight;
            builtWalkCore.CellWaterTruth = new(cellWaterTruth);
            builtWalkCore.WorldWaterTruth = wsWaterTruth;
            builtWalkCore.WorldDefaultWaterHeightTruth = wsDefaultWaterHeightTruth;
            builtWalkCore.CellsAtGrid = new(cellAtGrid);
            builtWalkCore.WorldChain = new(worldChain, StringComparer.OrdinalIgnoreCase);
            builtWalkCore.StationWorldspaceEditorId = stationWorldspaceEd;
            builtWalkCore.TerrainPatches = terrainPatches.Select(x =>
                new ViewProbe.TerrainPatchFact(x.Key, x.Value.ToArray())).ToArray();
            builtWalkCore.TerrainLayers = terrainLayers.Select(x =>
                new ViewProbe.TerrainLayerFact(x.Diffuse, x.Tris.ToArray())).ToArray();
            builtWalkCore.LandHeights = landHeights.Select(x =>
                new ViewProbe.LandHeightFact(x.Key.X, x.Key.Y, (float[,])x.Value.Clone(),
                    terrainExtras.Normals.TryGetValue(x.Key, out var fn) ? fn.ToArray() : null,
                    terrainExtras.Colors.TryGetValue(x.Key, out var fc) ? fc.ToArray() : null)).ToArray();
            cellScene!.StoreFacts(preparedKey!, builtWalkCore, factsProbe);
        }
        ViewProbe.CellFactsRed.ThrowAfterPublishIfRequested();
        ViewProbe.CellFactsRed.ThrowUnexpectedAfterPublishIfRequested();

        // Violet: not in the healthy palette (green/cyan/magenta/yellow/blue), not the reserved red,
        // not the white of the cell outline, not the orange of an implicated door.
        var diffColor = new System.Numerics.Vector4(0.72f, 0.28f, 1f, 1f);
        var noDiff = Array.Empty<System.Numerics.Vector3>();

        // ---- --grid: which PLANE the ruler is laid on ------------------------------------
        // Not a detail. A grid is a plane in the world, so one ruled at the wrong storey projects
        // to the wrong pixels and every coordinate read off it is wrong by exactly the parallax
        // the flag exists to remove. The chain is the storey the shot is ABOUT: the navmesh filter
        // first (if the overlay was cut to one floor, that floor is the subject), then the route's
        // floor, then the shot's measured one, and only then the aim point — which is a guess, and
        // is labelled as one in the picture as well as on the console.
        ViewProbe.TexturedScene.GridRender? gridRender = null;
        if (gridOn)
        {
            var (gz, gwhy) = navZAt is { } gnz ? (gnz, "--nav-z")
                           : routeZ is { } grz ? (grz, "--route-z")
                           : shotFloorZ is { } gfz ? (gfz, "the shot's floorZ")
                           : (lookPos.Value.Z, "ASSUMED: the aim point");
            gridRender = new ViewProbe.TexturedScene.GridRender(gridSpacing, gz, gridColor, gwhy);
        }

        // ---- --planes-report: one entry per plane — identity, provenance, what it culls from the
        // shot's eye, and the nearest solid collision behind it. A LISTING, never a verdict: the
        // audit that turns "culls nothing" and "nothing within reach behind it" into a class with a
        // floor and a control is the handoff after this one, and it needs the game's rule first.
        //
        // "Behind" is the far side from the eye: rays from a 5x5 lattice across the mid-quad (from
        // the far face, for a Box), along the plane's normal away from the eye, against the SOLID
        // collision (the layers an actor meets, the same cut the route detectors make) of every
        // physical ref whose placed OBND box the swept reach touches. The nearest hit over the
        // lattice is the answer; "none within N u" is an answer too, and the interesting one.
        Action<ViewProbe.OcclusionState>? planeReportSink = null;
        string? reportRenderPath = null;
        ViewProbe.DepthMeasurement? shotDepthMeasurement = null;   // this shot's --depth-measurement state; the report reads it
        if (planesReport)
        {
            var collGeo = new Dictionary<string, TriangleSoup?>(StringComparer.OrdinalIgnoreCase);
            TriangleSoup? CollisionSoupFor(string rel)
            {
                if (collGeo.TryGetValue(rel, out var cached)) return cached;
                TriangleSoup? soup = null;
                var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
                var res = assets.ResolveForPlacement(full);
                if (res.Sources.Count > 0)
                {
                    var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
                    if (bytes is not null)
                    {
                        var o = NifGeometry.Measure(bytes, includeTriangles: true);
                        var s = o.Model?.CollisionSoupFor(b => b.Solid && CollisionLayers.IsActorSolid(b.Layer));
                        if (s is { TriangleCount: > 0 and <= 300_000 }) soup = s;
                    }
                }
                collGeo[rel] = soup;
                return soup;
            }
            planeReportSink = os =>
            {
                var eyeS = eyePos.Value;
                var rows = new List<object>();
                // Each candidate body once; every plane below carries only its overlays (schema v2).
                var candidateBodies = ViewProbe.PlanesReportCandidates.Bodies(os.Refs, planes.Count);
                Console.WriteLine($"   ---- occlusion planes report: {planes.Count} plane(s) in {cellLabel}{(interior || !ringRefs ? "" : " + ring")}, "
                                + $"from eye {eyeS.X:0},{eyeS.Y:0},{eyeS.Z:0}; collision reach {planesReach:0} u behind each ----");
                int noCollisionCandidates = 0, noneWithinReach = 0, touching = 0, cullNothing = 0, cullsVisible = 0;
                foreach (var p in planes)
                {
                    string rot = $"{float.RadiansToDegrees(p.Rotation.X):0.#},{float.RadiansToDegrees(p.Rotation.Y):0.#},{float.RadiansToDegrees(p.Rotation.Z):0.#}°";
                    Console.WriteLine($"   PLANE {p.Key} = FormID {p.RuntimeId}  {p.KindLabel} {p.ExtentLabel}"
                                    + (p.Kind == ViewProbe.PlaneKind.PortalBox ? $" ({p.Bounds.Y:0.#} thick)" : "")
                                    + $"  at {p.Position.X:0},{p.Position.Y:0},{p.Position.Z:0}  rot {rot}"
                                    + (p.Scale != 1f ? $"  scale {p.Scale:0.##}" : "")
                                    + $"  — {p.ProvenanceDetail}  — chain: {p.ChainLabel}");
                    // What it culls from the shot's eye.
                    var culled = os.CulledByPlane.TryGetValue(p, out var cl) ? cl : new List<ViewProbe.RefOcclusion>();
                    int straddling = os.StraddlingByPlane.TryGetValue(p, out var st) ? st : 0;
                    var largest = culled.OrderByDescending(r => r.Volume).Take(3).ToList();
                    string Largest(ViewProbe.RefOcclusion r)
                        => $"{r.Ref.RefKey} {(r.Ref.BaseEditorId is { Length: > 0 } e ? e : Path.GetFileName(r.Ref.MeshRelPath))} ({r.Size.X:0}x{r.Size.Y:0}x{r.Size.Z:0} u)";
                    if (culled.Count == 0) cullNothing++;
                    bool VisibleHere(ViewProbe.RefOcclusion r) => r.Parts.Any(s => s.CulledBy.Contains(p) && s.Visible);
                    int visible = culled.Count(VisibleHere);
                    if (visible > 0) cullsVisible++;
                    var visibleLargest = culled.Where(VisibleHere).OrderByDescending(r => r.Volume).Take(3).ToList();
                    Console.WriteLine(culled.Count == 0
                        ? $"      no culling predicted from this eye; inspect unassessed candidates" + (straddling > 0 ? $" ({straddling} crossing its shadow)" : "")
                        : $"      culls {culled.Count} ref(s) from this eye ({straddling} straddling)"
                          + (visible > 0 ? $" — {visible} have visible pixels of a predicted-culled shape in this rendered scene: {string.Join("; ", visibleLargest.Select(Largest))}"
                                         : " — no measured visible shape pixels; inspect coverage before interpreting this")
                          + $"; largest: {string.Join("; ", largest.Select(Largest))}");
                    // The nearest solid collision behind it, on the far side from the eye.
                    var n = p.Normal;
                    float sideF = MathF.Sign(Vector3.Dot(eyeS - p.Centre, n));
                    if (sideF == 0f) sideF = 1f;
                    var far = -sideF * n;
                    float push = p.Kind == ViewProbe.PlaneKind.Box ? p.Bounds.Y * 0.5f * p.Scale : 0f;
                    var starts = ViewProbe.PlaneGeometry.GridPoints(p).Select(q => q + far * push).ToArray();
                    var sweep = Aabb.Empty;
                    foreach (var s0 in starts) { sweep = sweep.Add(s0); sweep = sweep.Add(s0 + far * planesReach); }
                    float best = float.PositiveInfinity; PlacedRef? bestRef = null; int candidates = 0, withCollision = 0;
                    foreach (var r in winRef.Values)
                    {
                        if (r.Deleted || DisabledWhy(r) is not null || r.Base is not { } bk) continue;
                        if (!baseType.TryGetValue(bk, out var bt) || !insidePhysical.Contains(bt)) continue;
                        if (baseIsMarker.Contains(bk)) continue;
                        if (baseEditorId.TryGetValue(bk, out var ed) && IsControlEditorId(ed)) continue;
                        if (!baseMesh.TryGetValue(bk, out var bm) || !baseObnd.TryGetValue(bk, out var ob)) continue;
                        var box = SpatialOverlap.ToWorld(new Aabb(ob.Min, ob.Max, false), r.Position, r.Rotation, r.Scale);
                        if (box.IsEmpty || SpatialOverlap.BoxDistance(box, sweep) > 0f) continue;
                        candidates++;
                        if (CollisionSoupFor(bm) is not { } soup) continue;
                        withCollision++;
                        var world = SpatialOverlap.TransformSoup(soup, r.Position, r.Rotation, r.Scale);
                        foreach (var s0 in starts)
                            if (NearestHit(s0, far, world.Vertices, world.Indices, MathF.Min(planesReach, best)) is { } h && h < best)
                            { best = h; bestRef = r; }
                    }
                    string behind;
                    if (bestRef is { } br)
                    {
                        string bEd = br.Base is { } bb2 && baseEditorId.TryGetValue(bb2, out var be2) ? be2 : "-";
                        if (best <= 4f) touching++;
                        behind = $"nearest solid collision behind it: {br.FormKey} {bEd} at {best:0.#} u{(best <= 4f ? " (touching)" : "")}"
                               + $"  [{candidates} candidate(s), {withCollision} with collision]";
                    }
                    else if (candidates == 0 || withCollision == 0)
                    {
                        noCollisionCandidates++;
                        behind = candidates == 0 ? $"legacy OBND broad phase supplied no candidate to the {planesReach:0} u ray sample; architecture absence is not established"
                                                 : $"none of the {candidates} candidate(s) behind it carries solid collision";
                    }
                    else
                    {
                        noneWithinReach++;
                        behind = $"no solid collision within {planesReach:0} u behind it ({candidates} candidate(s), {withCollision} with collision, none hit)";
                    }
                    Console.WriteLine($"      {behind}");
                    rows.Add(new
                    {
                        formKey = p.Key.ToString(), formId = p.RuntimeId, kind = p.KindLabel,
                        bounds = new[] { p.Bounds.X, p.Bounds.Y, p.Bounds.Z },
                        position = new[] { p.Position.X, p.Position.Y, p.Position.Z },
                        rotationDeg = new[] { float.RadiansToDegrees(p.Rotation.X), float.RadiansToDegrees(p.Rotation.Y), float.RadiansToDegrees(p.Rotation.Z) },
                        scale = p.Scale, provenance = p.ProvenanceLabel, definedBy = p.DefinedBy, changedBy = p.ChangedBy, chain = p.Chain,
                        cullCount = culled.Count, straddling, visibleCulled = visible,
                        candidates = ViewProbe.PlanesReportCandidates.Overlays(os.Refs, p, candidateBodies.Count),
                        largestCulled = largest.Select(r => new { formKey = r.Ref.RefKey.ToString(), editorId = r.Ref.BaseEditorId, size = new[] { r.Size.X, r.Size.Y, r.Size.Z }, visible = r.Visible }).ToList(),
                        visibleLargest = visibleLargest.Select(r => new { formKey = r.Ref.RefKey.ToString(), editorId = r.Ref.BaseEditorId, size = new[] { r.Size.X, r.Size.Y, r.Size.Z } }).ToList(),
                        behind = bestRef is { } br2
                            ? new { distance = best, formKey = br2.FormKey.ToString(), editorId = br2.Base is { } bb3 && baseEditorId.TryGetValue(bb3, out var be3) ? be3 : null, candidates, withCollision }
                            : null,
                        reach = planesReach, collisionMethod = "supporting 5x5 ray sample; legacy record-OBND broad phase; not an exhaustive architecture or visibility test",
                    });
                }
                Console.WriteLine($"   ---- research report: {cullNothing} of {planes.Count} plane(s) have no predicted culling; "
                                + $"{cullsVisible} predict culling of shapes with visible rendered samples ({os.VisibleCulledCount} unique ref(s)); inspect coverage; "
                                + $"{touching} touching solid collision behind, {noneWithinReach} with none within {planesReach:0} u, "
                                + $"{noCollisionCandidates} with no collision-bearing candidate behind at all ----");
                // The dropped planes belong in the listing too — a plane a mod disabled is a fact the audit wants.
                var dropped = winRef.Values.Where(r => r.Base is { } b && b == planeMarkerKey && (r.Deleted || DisabledWhy(r) is not null))
                                           .OrderBy(r => r.FormKey.ToString(), StringComparer.Ordinal).ToList();
                foreach (var r in dropped)
                {
                    var h = planeHist.TryGetValue(r.FormKey, out var ph2) ? ph2 : null;
                    Console.WriteLine($"   not drawn: {r.FormKey} = FormID {RuntimeId(r.FormKey)}  {(r.Deleted ? "deleted" : DisabledWhy(r))}"
                                    + (h is not null ? $"  — defined by {h.DefinedBy}{(h.ChangedBy is { } cb ? $", last changed by {cb}" : "")}  — chain: {string.Join(" -> ", h.Chain)}" : ""));
                }
                string jsonPath = Path.Combine(shotOutDir, $"{(reportRenderPath is null ? safe : Path.GetFileNameWithoutExtension(reportRenderPath))}-planes.json");
                if (File.Exists(jsonPath)) throw new System.IO.IOException("Refusing to replace a report: " + jsonPath);
                string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)); }
                var plugins = order.OrderedPaths.Select(p => new { file = Path.GetFileName(p), sha256 = HashFile(p) }).ToArray();
                var inputJson = System.Text.Json.JsonSerializer.Serialize(new { plugins, stateSpec, bosMode, keepFx, cullAbove, size,
                    visibilityBudget = os.VisibilityBudget,
                    ruleSha256 = ViewProbe.ShapeCullPolicy.Sha256,
                    cell = cellKey.ToString(), eye = new[] { eyeS.X, eyeS.Y, eyeS.Z },
                    look = new[] { lookPos.Value.X, lookPos.Value.Y, lookPos.Value.Z }, fov,
                    scene = os.Refs.Select(r => new { subject = r.Ref.RefKey.ToString(), r.Ref.Position, r.Ref.Rotation,
                        r.Ref.Scale, baseForm = r.Ref.BaseKey.ToString(), r.Ref.Constructed }).ToArray(),
                    planeInputs = planes.Select(p => new { key = p.Key.ToString(), p.KindLabel, p.Rotation, p.Scale,
                        position = new[] {p.Position.X,p.Position.Y,p.Position.Z}, bounds = new[] {p.Bounds.X,p.Bounds.Y,p.Bounds.Z} }).ToArray(),
                    assets = os.Refs.Select(r => new { mesh = r.Ref.MeshRelPath, sha256 = r.AssetHash,
                        issue = r.Issue }).Distinct().ToArray() });
                // The scene's scope, said where a reader of the verdicts looks (V1, lane V 2026-10-10): the ±2 game grid
                // the ring edge is drawn to is the engine default by inference, not a reading.
                if (!interior && ringRefs)
                    os.SceneIssues.Add($"scene drawn to the game's loaded grid ±{ViewProbe.WinningRefWalk.EdgeRadius} by inference "
                                     + "(uGridsToLoad unset in the INIs, not read in game); large references the worldspace's lists name are drawn to "
                                     + $"±{ViewProbe.WinningRefWalk.LargeRadius} when their bounds enter the 3x3 (measured in game); content beyond the 3x3 is not drawn");
                var sceneScope = interior ? null : new
                {
                    rule = ringRefs ? "target cell, its ±1 ring, and references anchored up to edgeRadius cells out whose placed bounds enter the 3x3"
                                    : "target cell only (--no-ring-refs)",
                    edgeRadius = ringRefs ? ViewProbe.WinningRefWalk.EdgeRadius : 0,
                    edgeRadiusBasis = "inferred",
                    edgeAdmitted = edgeAdmittedCount, edgePlanesAdmitted = edgePlanesAdmittedCount,
                    largeRadius = ringRefs ? ViewProbe.WinningRefWalk.LargeRadius : 0,
                    largeRadiusBasis = "measured in game: Tamriel (one station) and both city worldspaces",
                    largeList = largeIsChild && !largeChildUnion ? "none" : "union",
                    largeListBasis = largeIsChild ? "child worldspace: its own lists (--large-refs-child)" : "root worldspace: every plugin's list",
                    largeListed = largeListedCount, largeAdmitted = largeAdmittedCount, largePlanesAdmitted = largePlanesAdmittedCount,
                    largePrimitives = "by record",
                };
                var doc = new
                {
                    schema = ViewProbe.PlanesReportCandidates.Schema, status = "research; supplied rule scope does not certify general engine behavior",
                    rule = ViewProbe.ShapeCullPolicy.Identity, scope = ViewProbe.ShapeCullPolicy.Scope,
                    ruleSha256 = ViewProbe.ShapeCullPolicy.Sha256, ruleConfiguration = ViewProbe.ShapeCullPolicy.Configuration,
                    inputSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inputJson))),
                    inputIdentityScope = "record files, source mesh bytes, effective scene placements, planes, camera, state and visibility budget; texture/material assets not yet fingerprinted",
                    appearance = shotDepthMeasurement?.Appearance() ?? (object)new { status = "unassessed", reasonCode = "appearance_inputs_not_fingerprinted",
                        reason = "Texture/material inputs and renderer fallbacks are not qualified; geometry visibility is not a claim of matching game appearance." },
                    plugins, assemblies = new[] { typeof(ViewProbe.ShapeOcclusion).Assembly.Location, typeof(SphereBound).Assembly.Location,
                        typeof(SkyRender.GlMesh).Assembly.Location }
                        .Select(p => new { file = Path.GetFileName(p), sha256 = HashFile(p) }).ToArray(),
                    cell = cellLabel, cellFormKey = cellKey.ToString(), worldspace = targetWorldspace.ToString(),
                    state = stateSpec ?? "load-time", stateView, bosMode,
                    eye = new[] { eyeS.X, eyeS.Y, eyeS.Z }, look = new[] { lookPos.Value.X, lookPos.Value.Y, lookPos.Value.Z },
                    fov, aspect = Math.Round((double)size / canvasH, 6), rasterSize = size, rasterHeight = canvasH,
                    nearClip = 15, farClip = 131072, reach = planesReach,
                    viewpoint = shotSpec?.ViewpointKind ?? "diagnostic; reachability not established",
                    control = shotSpec?.Control ?? false,
                    viewpointEvidence = shotSpec?.ViewpointEvidence, viewLabel = shotSpec?.Label,
                    image = reportRenderPath, replayArgs = shotReplay, coverageIssues = os.SceneIssues, sceneScope,
                    population = new { recordRefs = winRef.Count, sceneRefs = os.Refs.Count,
                        sourceShapes = os.Refs.Sum(r => r.Parts.Count), noModel, fxSkipped, markerSkipped,
                        unsupportedRefs = os.Refs.Count(r => r.Issue is not null),
                        unsupportedShapes = os.Refs.Sum(r => r.Parts.Count(s => s.Issue is not null)),
                        visibilityDraws = os.VisibilityDraws, visibilityBudget = os.VisibilityBudget },
                    counts = new { sampledViews = 1, planes = planes.Count, predictedAffectedUniqueRefs = os.CulledCount,
                        visiblePredictedUniqueRefs = os.VisibleCulledCount,
                        constructedVisibleUniqueRefs = os.Refs.Count(r => r.Ref.Constructed && r.Visible),
                        nonControlVisibleUniqueRefs = (shotSpec?.Control ?? false) ? 0 : os.Refs.Count(r => !r.Ref.Constructed && r.Visible) },
                    sceneDepthSha256 = os.SceneDepthHash, sceneDepth = os.SceneDepthPath,
                    visibilityMethod = "same shape isolated depth against same-camera unculled scene; single sample; exact depth comparison; green mask pixels visible, red hidden",
                    attribution = "none; plane provenance does not establish blame or a repair",
                    contestedBy = cellContestedBy, candidateBodies, planes = rows,
                    notDrawn = dropped.Select(r => new { formKey = r.FormKey.ToString(), formId = RuntimeId(r.FormKey), why = r.Deleted ? "deleted" : DisabledWhy(r) }).ToList(),
                    // Null on a healthy run - and WRITTEN as null, one line this report did not
                    // carry before the check existed. That is deliberate: an absent key means a
                    // build that never looked, null means it looked and found none, and an object
                    // names every asset that was found and could not be read and says whether an
                    // opt-out let the run succeed anyway. See UnreadableAssets.ReportBlock.
                    unreadableAssets = ViewProbe.UnreadableAssets.ReportBlock(),
                    // Null when no terrain texture layer failed to read (also when no terrain walk ran); see PluginRead.cs.
                    terrainLayersUnreadable = ViewProbe.TerrainLayerFailures.ReportBlock(),
                };
                // Straight to the file rather than to a string first: this document is ~600 MB of
                // indented JSON and the string was a gigabyte of UTF-16 on top of it. The bytes are
                // the same bytes, and the file only takes its own name once it is complete.
                ViewProbe.ReportSink.Write(jsonPath, doc);
                if (collectArtifacts)
                {
                    completedArtifacts.AddClosed(jsonPath, "report");
                    if (os.SceneDepthPath is not null) completedArtifacts.AddClosed(os.SceneDepthPath, "depth");
                    foreach (var mask in os.Refs.SelectMany(r => r.Parts).Select(s => s.VisibilityMask)
                                 .Where(p => p is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                        completedArtifacts.AddClosed(mask, "mask");
                }
                Console.WriteLine($"   wrote {jsonPath}");
            };
        }

        int Render(IReadOnlyList<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)> g,
                   IReadOnlyList<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)> r,
                   string outFile, IReadOnlyList<string> captionLines,
                   IReadOnlyList<System.Numerics.Vector3>? diffs = null)
        {
            reportRenderPath = outFile;
            shotDepthMeasurement = depthMeasurement ? new ViewProbe.DepthMeasurement { KeepAllMasks = keepAllMasks, OcclusionQuery = occlusionQuery } : null;
            int rendered = ViewProbe.TexturedScene.RenderToPng(
                   scene, assets, dataDir, modFolders, overwriteDir,
                   (eyePos.Value, lookPos.Value, fov), g, r, terrainPatches, terrainLayers, navMode, navColor,
                   skyColor, terrainHeightAt, dashUnderground, dashUnderStatics, msaa, cullAbove, cellBounds,
                   diffs ?? noDiff, diffColor, picks, captionLines, colourCaptions, npcActors, npcPucksOn, routeRenders,
                   gridRender, overlayLines, keepFx, fxStatic, listMeshes, waterPlanes, coverings, planes, showPlanes, occlusionMode,
                   size, outFile, reportMode, camMeta, planeReport: planeReportSink, bosMode: bosMode, stateMode: stateView, stateName: stateArm?.Name ?? "",
                   visibilityBudget: visibilityBudget, navColourGroups: navColourGroups, refSurface: refSurface,
                   refSurfaceOpacity: refSurfaceOpacity,
                   depthDumpPath: depthDump, sizeHeight: canvasH, cellScene: cellScene,
                   graphicsReuseRed: graphicsReuseRed, graphicsCell: graphicsCell,
                   preparedIdentity: preparedKey, depthMeasurement: shotDepthMeasurement, terrainExtras: terrainExtras);
            if (collectArtifacts) completedArtifacts.AddClosed(outFile, "frame");
            return rendered;
        }

        if (compare is { } cmp)
        {
            // Three panels, one camera: A's navmesh, B's navmesh, the winning state. The world
            // geometry and terrain are the winning state in all three — only the navmesh layer
            // changes, which is exactly the thing being compared.
            // Each side panel is the cell's FULL navmesh as if that plugin won: the winning state
            // with the plugin's own versions substituted in. Records neither plugin touches are then
            // IDENTICAL in all three panels, so any visible difference is the actual disagreement.
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
                string pf = Path.Combine(shotOutDir, $"{safe}-cmp{pi + 1}-{new string(label.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray())}.png");
                var cap = new List<string> { $"[{pi + 1}/3] {(pi == 2 ? "winning state (actual merged navmesh)" : $"cell navmesh if {label} won")} \u2014 {cellLabel} \u2014 {camPose}{CullCaption()}" };
                if (redNav.Count > 0) cap.Add(RedCaption(navs));
                if (DoorCaption() is { } dc) cap.Add(dc);
                if (showPlanes && planeLegend.Length > 0) cap.Add(planeLegend);
                if (bosLayer is not null && bosSwapped > 0) cap.Add($"BOS {bosMode}: {bosSwapped} swapped ref(s), {bosRandomCount} random{(bosMode == "diff" ? " - tinted TEAL" : "")}");
                if (stateArm is not null) cap.Add($"STATE {stateArm.Name} {stateView}: {stateArrived} arrive, {stateDeparted} depart{(stateView == "diff" ? " - GREEN in, RED out" : "")}");
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
            string stitched = Path.Combine(shotOutDir, $"{safe}-compare.png");
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
            if (collectArtifacts) completedArtifacts.AddClosed(stitched, "frame");
            Console.WriteLine($"wrote:\n   {stitched}");
            Console.WriteLine($"   panels left->right: [1] {cmp.A}   [2] {cmp.B}   [3] winning state (+conflict red)");
            return 0;
        }

        if (navPlugin is not null)
        {
            var navs = navByPlugin.TryGetValue(navPlugin, out var d0) ? d0 : new Dictionary<FormKey, NavmeshGeometry>();
            Console.WriteLine($"   navmesh source: {navPlugin} ({navs.Count} record version(s) in this cell)");
            (navTris, redTris) = BuildNavSets(navs);
        }

        string fpOut = Path.Combine(shotOutDir, $"{safe}-fp.png");
        var fpCap = new List<string> { $"{cellLabel} \u2014 {navLayerLabel} \u2014 {camPose}{CullCaption()}" };
        if (redNav.Count > 0) fpCap.Add(RedCaption(displayNav));
        if (DoorCaption() is { } fdc) fpCap.Add(fdc);
        if (showPlanes && planeLegend.Length > 0) fpCap.Add(planeLegend);
        if (bosLayer is not null && bosSwapped > 0) fpCap.Add($"BOS {bosMode}: {bosSwapped} swapped ref(s), {bosRandomCount} random{(bosMode == "diff" ? " - tinted TEAL" : "")}");
        if (stateArm is not null) fpCap.Add($"STATE {stateArm.Name} {stateView}: {stateArrived} arrive, {stateDeparted} depart{(stateView == "diff" ? " - GREEN in, RED out" : "")}");

        if (interactiveMode)
        {
            // Same build, different ending: the scene stays resident and the window opens. The
            // navmesh identity list mirrors exactly what BuildNavSets admits to the overlay
            // (same source dict, same deleted/out-of-range filters), so a hover can only name
            // triangles that are actually drawn.
            var navSrc = navPlugin is not null && navByPlugin.TryGetValue(navPlugin, out var nsrc)
                ? (IReadOnlyDictionary<FormKey, NavmeshGeometry>)nsrc : evaluatedNav;
            var iopt = new ViewProbe.InteractiveOptions
            {
                Window = fpGw,
                RuntimeId = RuntimeId,
                TerrainDescribe = terrainDescribeAt,
                CellLabel = cellLabel,
                CellKey = cellKey,
                Worldspace = targetWorldspace,
                Interior = interior,
                Grid = interior ? null : targetGrid,
                RefCells = refCellOf,
                RefOutcomes = refOutcome,
                OutDir = shotOutDir,
                SafeName = safe,
                RedNav = redNav.Keys.ToHashSet(),
                FlySpeed = flySpeed,
                LookSens = lookSens,
                // PageUp/PageDown step THIS cell's shots for free (everything is resident);
                // Ctrl+ steps cells, which only the run loop above can serve — and only when
                // the run actually has a worklist to step through.
                // The route rides into the window as overlay geometry (nothing extra to build) —
                // these are only what R and the HUD need to talk about it.
                RouteCount = routeRenders.Sum(rt => rt.Waypoints.Count),
                RouteLabel = routeLabel,
                Worklist = worklist,
                WorklistCell = wlCell,
                WorklistShot = wlShot,
                CanSwitchCell = worklist is not null && worklist.Cells.Count > 1,
                // Announce the build-time knife in the HUD. The live clip keys cannot undo it, so
                // without this the bar can read "clip: off" over a scene whose roofs were removed
                // before the window existed.
                BuildCullAbove = cullAbove,
                BuildNavZ = navZAt is { } bnz ? (bnz, navZTol) : null,
                // The planes layer and the occlusion preview, live: L and O in the window, seeded
                // from the command line (or the shot) the way the nav mode is.
                PlaneCount = planes.Count,
                ShowPlanes = showPlanes,
                PlaneLegend = planeLegend,
                ReportMode = reportMode,
                // read by a host's layer switches (the workbench's view toolbar)
                TerrainHeightAt = terrainHeightAt,
                LandCells = landHeights.Keys.ToList(),   // --live narrows it to the loaded square below
                BaseTypes = baseType,
                UndrawnAt = refUndrawnAt,
            };
            if (navMode != "off")
                foreach (var nav in navSrc.Values)
                    for (int ti = 0; ti < nav.Triangles.Count; ti++)
                    {
                        var tri = nav.Triangles[ti];
                        if ((tri.Flags & 0x08) != 0) continue;
                        if (tri.A < 0 || tri.B < 0 || tri.C < 0) continue;
                        if (tri.A >= nav.Vertices.Count || tri.B >= nav.Vertices.Count || tri.C >= nav.Vertices.Count) continue;
                        if (!NavZKeeps(nav, tri)) continue;
                        iopt.NavTris.Add((nav.FormKey, ti, V(nav.Vertices[tri.A]), V(nav.Vertices[tri.B]), V(nav.Vertices[tri.C])));
                    }

            // A host frames its own camera once the scene is built (InteractiveViewer.Host). Its placeholder pose at the
            // origin would size the overlays built for the entry camera (navmesh wire dashes, camera-facing ribbons) from
            // tens of thousands of units away in an exterior, so the entry camera stands over the target cell instead.
            var entryEye = eyePos.Value; var entryLook = lookPos.Value;
            if (ViewProbe.InteractiveViewer.Host is not null && entryEye == System.Numerics.Vector3.Zero && !interior && targetGrid is { } hostGrid)
            {
                float hcx = hostGrid.X * 4096f + 2048f, hcy = hostGrid.Y * 4096f + 2048f, hgz = terrainHeightAt?.Invoke(hcx, hcy) ?? 0f;
                entryLook = new(hcx, hcy, hgz); entryEye = new(hcx - 1100f, hcy - 3000f, hgz + 2700f);
            }
            // --live (LiveScene.cs): the walk prepared the whole --ring square; the first build draws the loaded square (the
            // profile's uGridsToLoad around the target) and the host adds or drops the other cells of the square live.
            IReadOnlyList<ViewProbe.SceneRef> firstScene = scene;
            IReadOnlyDictionary<string, List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)>> firstPatches = terrainPatches;
            IReadOnlyList<(string Diffuse, List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C, float Oa, float Ob, float Oc)> Tris)> firstLayers = terrainLayers;
            IReadOnlyList<ViewProbe.WaterPlane> firstWater = waterPlanes;
            if (liveScene && ViewProbe.InteractiveViewer.Host is not null && !interior && targetGrid is { } liveGrid && visibleSet is { } reach)
            {
                int grids = ViewProbe.ProfileIni.Int("General", "uGridsToLoad", 5, out var gridsWhy);
                if (grids < 1 || grids % 2 == 0)
                {
                    gridsWhy += $"; the game loads an odd square, so {grids} is read as {Math.Max(1, grids | 1)}";
                    grids = Math.Max(1, grids | 1);
                }
                int half = grids / 2;
                var loaded = reach.Where(c => Math.Abs(c.X - liveGrid.X) <= half && Math.Abs(c.Y - liveGrid.Y) <= half).ToHashSet();
                var allGrass = ViewProbe.GrassLayer.Cells.ToList();
                var live = new ViewProbe.LiveSlicer(scene, terrainPatches, terrainLayers, waterPlanes);
                // The first build draws no cell: every cell is a slice the host adds (and can drop again), the loaded square first.
                (firstScene, firstPatches, firstLayers, firstWater) = live.For(Array.Empty<(int X, int Y)>());
                ViewProbe.GrassLayer.Cells.Clear();
                iopt.Reach = reach; iopt.Loaded = loaded; iopt.GridsToLoad = grids; iopt.GridsToLoadSource = gridsWhy;
                // which square each walked cell is: where its references stand. A cell is named only when at least 80% of them
                // stand in one square, which leaves out the worldspace's persistent cell (its references are everywhere) and
                // a cell placing nothing.
                iopt.CellGrids = refCellOf.Where(kv => winRef.ContainsKey(kv.Key))
                    .GroupBy(kv => kv.Value, kv => ViewProbe.LiveSlicer.CellOf(winRef[kv.Key].Position.X, winRef[kv.Key].Position.Y))
                    .Select(g => (Cell: g.Key, Total: g.Count(), Top: g.GroupBy(c => c).OrderByDescending(c => c.Count()).First()))
                    .Where(x => x.Top.Count() >= 0.8 * x.Total)
                    .ToDictionary(x => x.Cell, x => x.Top.Key);
                if (cellKey is { } ck) ((Dictionary<FormKey, (int X, int Y)>)iopt.CellGrids)[ck] = liveGrid;
                iopt.LandCells = new List<(int X, int Y)>();
                Console.WriteLine($"   live scene: {reach.Count} cell(s) prepared; the host loads {grids}x{grids} of them ({gridsWhy}) and adds or drops cells live");
                iopt.PrefetchCell = (cell, parallel, ct) => iopt.Live is { } ps && reach.Contains(cell)
                    ? ViewProbe.TexturedScene.Prefetch(ps, live.For(new[] { cell }).Refs, parallel, ct) : 0;
                iopt.BuildCell = cell =>
                {
                    if (!reach.Contains(cell) || iopt.Live is not { } session) return null;
                    var one = new HashSet<(int X, int Y)> { cell };
                    var (cScene, cPatches, cLayers, cWater) = live.For(one);
                    ViewProbe.GrassLayer.Cells.Clear(); ViewProbe.GrassLayer.Cells.AddRange(allGrass.Where(c => c.GridX == cell.X && c.GridY == cell.Y));
                    var collect = new ViewProbe.InteractiveOptions { Window = iopt.Window, RuntimeId = iopt.RuntimeId, CellLabel = iopt.CellLabel, OutDir = iopt.OutDir, SafeName = iopt.SafeName };
                    var added = new List<SkyRender.GlMesh>();
                    var cellSw = System.Diagnostics.Stopwatch.StartNew();
                    ViewProbe.TexturedScene.RenderToPng(
                        cScene, assets, dataDir, modFolders, overwriteDir,
                        (entryEye, entryLook, fov), Array.Empty<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)>(),
                        Array.Empty<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)>(), cPatches, cLayers, "off", navColor,
                        skyColor, terrainHeightAt, false, false, msaa, cullAbove, null,
                        noDiff, diffColor, new List<(int X, int Y)>(), new List<string>(), new List<(string Text, System.Numerics.Vector3 Color)>(),
                        new List<ViewProbe.NpcActorRender>(), false, new List<ViewProbe.TexturedScene.RouteRender>(), null,
                        new List<ViewProbe.TexturedScene.OverlayLine>(), keepFx, fxStatic, false,
                        cWater, coverings, new List<ViewProbe.PlaneRender>(), false, "off", size, fpOut,
                        reportMode, null, collect, null, bosMode, stateView, stateArm?.Name ?? "",
                        refSurface: refSurface, refSurfaceOpacity: refSurfaceOpacity, terrainExtras: terrainExtras,
                        live: session, liveMeshes: added);
                    var made = new ViewProbe.LiveCell
                    {
                        Grid = cell, PickShapes = collect.PickShapes, Refs = collect.Occlusion?.Refs.ToList() ?? new(),
                        LayerMeshes = collect.LayerMeshes, HasLand = landHeights.ContainsKey(cell), Seconds = cellSw.Elapsed.TotalSeconds,
                    };
                    made.Meshes.AddRange(added);
                    return made;
                };
            }
            int iShapes = ViewProbe.TexturedScene.RenderToPng(
                firstScene, assets, dataDir, modFolders, overwriteDir,
                (entryEye, entryLook, fov), navTris, redTris, firstPatches, firstLayers, navMode, navColor,
                skyColor, terrainHeightAt, dashUnderground, dashUnderStatics, msaa, cullAbove, cellBounds,
                noDiff, diffColor, picks, fpCap, colourCaptions, npcActors, npcPucksOn, routeRenders, gridRender, overlayLines, keepFx, fxStatic, listMeshes,
                firstWater, coverings, planes, showPlanes, occlusionMode, size, fpOut,
                reportMode, camMeta, iopt, planeReportSink, bosMode, stateView, stateArm?.Name ?? "", navColourGroups: navColourGroups,
                refSurface: refSurface, refSurfaceOpacity: refSurfaceOpacity, graphicsReuseRed: graphicsReuseRed,
                graphicsCell: graphicsCell, preparedIdentity: preparedKey, terrainExtras: terrainExtras);
            Console.WriteLine($"   interactive run over ({iShapes:N0} shapes were resident)");
        pendingSwitch = iopt.CellSwitch;
            return 0;
        }

        int drawnShapes = Render(navTris, redTris, fpOut, fpCap);
        Console.WriteLine($"wrote:\n   {fpOut}");
        Console.WriteLine($"   eye {eyePos.Value.X:0},{eyePos.Value.Y:0},{eyePos.Value.Z:0} -> look {lookPos.Value.X:0},{lookPos.Value.Y:0},{lookPos.Value.Z:0}"
                        + $"  yaw {camYawDeg:0.#}° pitch {camPitchDeg:0.#}° fov {fov:0}°  ({drawnShapes:N0} shapes)");
        return 0;
    }

    // Not a textured run: the flat diagnostic mode below is this call's continuation.
    return -1;
}

// ---------------------------------------------------------------------------------------
// THE RUN
// ---------------------------------------------------------------------------------------
// Q3: a flag that quietly does nothing is a wrong answer. The lease is a WORKLIST's, and only on
// the static path — --interactive already keeps one cell resident by design, and a single shot has
// nobody to share with.
if (reuseCellExplicit && (worklist is null || interactiveMode))
    Console.WriteLine("   --reuse-cell: ignored here. It shares a cell's work between the SHOTS of "
                    + "a worklist on the static path; this run has "
                    + (worklist is null ? "one shot." : "--interactive, which already keeps a cell resident."));

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
            int rc; bool abandoned = false;
            try { rc = RunCellTextured(worklist.Cells[wlc].Cell, wlc, wls, null, out _); }
            catch (ViewProbe.BuildAbandoned)
            {
                // A host dropped this build for another request. A first walk cut short leaves the BOS form index partial,
                // and it fills on the first walk only, so it starts again.
                abandoned = true; rc = 0;
                if (bosIdx is not null && bosLayer is null) { bosIdx = new BosFormIndex(); bosLight.Clear(); }
            }
            catch (ViewProbe.GlAllocationRefusal ex) { Console.Error.WriteLine(ex.Message); return 2; }   // F-G4-1
            catch (ViewProbe.CaptureRefusal ex) { Console.Error.WriteLine(ex.Message); return 2; }        // CaptureViews.cs
            catch (ViewProbe.ReadRefusal ex) { ViewProbe.PluginRead.Report(ex); return 2; }   // PluginRead.cs
            // A host's request for a cell this worklist may not hold (InteractiveViewer.CellRequest): it joins the list.
            if (ViewProbe.InteractiveViewer.TakeCellRequest() is { } asked)
            {
                var askedNav = ViewProbe.InteractiveViewer.TakeCellRequestNav();
                int at = -1;
                for (int ci = 0; ci < worklist.Cells.Count; ci++)
                    if (string.Equals(worklist.Cells[ci].Cell, asked, StringComparison.OrdinalIgnoreCase)) { at = ci; break; }
                if (at < 0)
                {
                    worklist = worklist with { Cells = worklist.Cells.Append(new HarnessShared.WorklistCell(asked, new[] { new HarnessShared.WorklistShot(
                        "placeholder pose: the host frames the camera once the cell is built", new[] { 0f, 0f, 0f }) { Nav = askedNav } })).ToList() };
                    at = worklist.Cells.Count - 1;
                }
                else if (askedNav is not null)
                {
                    // the host asks this cell again with another navmesh mode: its shots carry it
                    var cellAt = worklist.Cells[at];
                    var cells = worklist.Cells.ToList();
                    cells[at] = cellAt with { Shots = cellAt.Shots.Select(sh => sh with { Nav = askedNav }).ToList() };
                    worklist = worklist with { Cells = cells };
                }
                (wlc, wls) = (at, 0);
                continue;
            }
            if (abandoned) return 0;
            if (pendingSwitch is not { } next) return rc < 0 ? 0 : rc;
            (wlc, wls) = next;
        }
    }

    // Static: every shot of every cell, each to its own PNG. The cell lease (the default)
    // retains the complete owned record-walk value; --no-reuse-cell runs walk once per shot.
    int totalShots = worklist.Cells.Sum(c => c.Shots.Count);
    if (totalShots > worklist.Cells.Count)
        Console.WriteLine($"worklist: {totalShots} shots over {worklist.Cells.Count} cell(s) — "
                        + (reuseCell
                            ? "--reuse-cell retains one owned record-walk result per equal cell/input key."
                            : "the load walk runs once PER SHOT in static mode (use --interactive to pay it once per cell)."));
    try
    {
        for (int wlc = 0; wlc < worklist.Cells.Count; wlc++)
        {
            // Do not construct a cell lease for a cell with no selected coordinate. The original
            // worklist remains intact; this is only a dispatch decision over its original indices.
            if (batchProgress is not null
                && !Enumerable.Range(0, worklist.Cells[wlc].Shots.Count).Any(s => batchProgress.Contains(wlc, s)))
                continue;
            // --reuse-cell: ONE lease per cell, constructed here and disposed in the finally — a scope,
            // not a cache that gets cleared, so nothing built for this cell can answer in the next one.
            // With --no-reuse-cell the lease is null and every shot builds its own, as before stage 4.
            int cellBoundaryExit = 0;
            var cleanupFailures = new List<Exception>();
            int cellShots = 0;              // resident telemetry (VP_RESIDENT_TELEMETRY_OUT): shots this cell started
            string cellOutcome = "fault";   // ...and how its shot loop ended: completed, stopped (a nonzero shot) or fault
            try
            {
                cellScene = reuseCell ? new ViewProbe.CellScene(worklist.Cells[wlc].Cell) : null;
                graphicsReuseRed = ViewProbe.GraphicsReuseRed.TryBeginCell(worklist.Cells[wlc].Cell, wlc);
                if (reuseCell)
                {
                    var graphicsSettings = new NativeWindowSettings
                    {
                        ClientSize = new OpenTK.Mathematics.Vector2i(8, 8), StartVisible = false,
                        StartFocused = false, WindowBorder = WindowBorder.Hidden,
                        APIVersion = new Version(3, 3), Profile = ContextProfile.Core,
                    };
                    // The pinned budget is a RED-session value only (G4 pin arm); null otherwise, as before.
                    graphicsCell = new ViewProbe.GraphicsCell(graphicsSettings, graphicsReuseRed, graphicsReuseRed?.TestBudgetBytes);
                }
                for (int wls = 0; wls < worklist.Cells[wlc].Shots.Count; wls++)
                {
                    if (batchProgress is not null && !batchProgress.Contains(wlc, wls)) continue;
                    cellShots++;
                    int rc;
                    ViewProbe.BatchProgress.ShotArtifacts completed;
                    try
                    {
                        var plan = batchProgress?.PlanFor(wlc, wls);
                        rc = RunCellTextured(worklist.Cells[wlc].Cell, wlc, wls, plan, out completed);
                    }
                    catch (ViewProbe.CaptureRefusal ex)   // CaptureViews.cs: --protect, or a label that cannot be drawn
                    {
                        Console.Error.WriteLine(ex.Message);
                        rc = 2; completed = new ViewProbe.BatchProgress.ShotArtifacts();
                    }
                    catch (ViewProbe.GlAllocationRefusal ex)   // F-G4-1: refused before draw, in every mode
                    {
                        Console.Error.WriteLine(ex.Message);
                        rc = 2; completed = new ViewProbe.BatchProgress.ShotArtifacts();
                    }
                    catch (ViewProbe.ReadRefusal ex)   // a plugin or record that cannot be read (PluginRead.cs)
                    {
                        ViewProbe.PluginRead.Report(ex);
                        rc = 2; completed = new ViewProbe.BatchProgress.ShotArtifacts();
                    }
                    catch (Exception ex) when (ViewProbe.GraphicsReuseRed.IsExpectedFault(ex))
                    {
                        Console.Error.WriteLine(ex);
                        ViewProbe.GraphicsReuseRed.CaughtExpectedFault(ex);
                        rc = 2; completed = new ViewProbe.BatchProgress.ShotArtifacts();
                    }
                    catch (Exception ex) when (ViewProbe.CellFactsRed.IsExpectedFault(ex))
                    {
                        // Test-only exception boundary: record the full exception and preserve the
                        // production path's nonzero refusal without letting an intentional RED reach
                        // Windows' unhandled-exception dialog. The cell finally below still owns cleanup.
                        ViewProbe.CellFactsRed.CaughtExpectedFault(ex);
                        rc = 2; completed = new ViewProbe.BatchProgress.ShotArtifacts();
                    }
                    catch (Exception ex) when (ViewProbe.GraphicsReuseRed.TestBoundaryEnabled)
                    {
                        Console.Error.WriteLine(ex);
                        ViewProbe.GraphicsReuseRed.CaughtUnexpected(ex);
                        rc = 2; completed = new ViewProbe.BatchProgress.ShotArtifacts();
                    }
                    catch (Exception ex) when (ViewProbe.CellFactsRed.TestBoundaryEnabled)
                    {
                        // Any unexpected exception in a test-enabled run is captured with its full
                        // stack and converted to a normal nonzero exit. Production keeps its existing
                        // exception behavior because this boundary is absent without the test sink.
                        ViewProbe.CellFactsRed.CaughtUnexpected(ex);
                        rc = 2; completed = new ViewProbe.BatchProgress.ShotArtifacts();
                    }
                    if (rc > 0)
                    {
                        cellBoundaryExit = ViewProbe.ExtractionScratch.GuardExitCode(rc);
                        break;
                    }
                    if (batchProgress is not null && rc == 0)
                    {
                        if (ViewProbe.BatchProgress.ShotMayCommit(ViewProbe.UnreadableAssets.Count,
                                                                 ViewProbe.UnreadableAssets.OptOutSource))
                            batchProgress.CommitShot(wlc, wls, worklist.Cells[wlc].Shots[wls].Label, completed);
                        else
                            Console.Error.WriteLine($"batch progress: no success receipt for original c{wlc + 1}s{wls + 1}; "
                                + $"this process has {ViewProbe.UnreadableAssets.Count} unsuppressed unreadable asset(s), "
                                + $"including archive extraction failures not represented by ShotEntries. Exit {ViewProbe.UnreadableAssets.FailExitCode} "
                                + "will be spent after the remaining shots.");
                    }
                }
                graphicsReuseRed?.AfterShotLoop();   // test-only (B10b RED): the cell-level fault point; inert without a RED session
                cellOutcome = cellBoundaryExit > 0 ? "stopped" : "completed";
            }
            catch (Exception ex) when (ViewProbe.GraphicsReuseRed.TestBoundaryEnabled)
            {
                Console.Error.WriteLine(ex);
                try { ViewProbe.GraphicsReuseRed.CaughtUnexpected(ex); }
                catch (Exception logEx) { Console.Error.WriteLine(logEx); }
                cellBoundaryExit = 2;
            }
            finally
            {
                void CaptureCleanup(Action cleanup)
                {
                    try { cleanup(); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(ex);
                        if (ViewProbe.GraphicsReuseRed.TestBoundaryEnabled)
                        {
                            try { ViewProbe.GraphicsReuseRed.CaughtUnexpected(ex); }
                            catch (Exception logEx) { Console.Error.WriteLine(logEx); }
                            cellBoundaryExit = 2;
                        }
                        else cleanupFailures.Add(ex);
                    }
                }
                // At cell end, on every exit path, before the cell's owners are disposed; a no-op unless enabled.
                ViewProbe.ResidentTelemetry.WriteCell(wlc, worklist.Cells[wlc].Cell, reuseCell, cellOutcome, cellBoundaryExit,
                    cellShots, graphicsCell);
                CaptureCleanup(() => graphicsCell?.Dispose());
                graphicsCell = null;
                CaptureCleanup(() => cellScene?.Dispose());
                cellScene = null;
                CaptureCleanup(() => graphicsReuseRed?.Dispose());
                graphicsReuseRed = null;
            }
            if (cleanupFailures.Count > 0)
                throw new AggregateException("worklist cell cleanup failed", cleanupFailures);
            if (cellBoundaryExit > 0)
            {
                _ = ViewProbe.GraphicsReuseRed.Finish();
                return ViewProbe.ExtractionScratch.GuardExitCode(cellBoundaryExit);
            }
        }
    }
    catch (Exception ex) when (batchProgress is not null && !ViewProbe.GraphicsReuseRed.TestBoundaryEnabled)
    {
        // The opt-in driver boundary includes CellScene construction, rendering, receipt commit
        // and the lease's finally. Every failure is durable console text plus a normal nonzero exit,
        // never a Windows unhandled-exception dialog.
        Console.Error.WriteLine("worklist selection/progress failed:\n" + ex);
        return 2;
    }
    // GuardExitCode, not a bare 0. The extraction guard throws, and two of this file's own callers
    // catch Exception and carry on, so whether the refusal reaches here is call-order luck. The
    // refusal is latched where it happens; this is where a latched refusal becomes an exit code
    // that no catch can improve on.
    int worklistExit = ViewProbe.ExtractionScratch.GuardExitCode(ViewProbe.UnreadableAssets.FinalExitCode(0));
    if (!ViewProbe.CellFactsRed.Finish()) return 1;
    if (!ViewProbe.GraphicsReuseRed.Finish()) return 1;
    if (ViewProbe.ResidentTelemetry.Failed && worklistExit == 0) return 2;   // a telemetry write failed; it printed why
    return worklistExit;
}

// --winning-refs-dump: the real walk for each target in turn, one JSON line each, nothing drawn.
if (winRefsDump is not null)
{
    var dumpTargets = dumpCellsFile is not null ? ViewProbe.WinningRefsDump.ReadCells(dumpCellsFile) : new List<string> { target };
    for (int di = 0; di < dumpTargets.Count; di++)
    {
        int drc;
        try { drc = RunCellTextured(dumpTargets[di], 0, 0, null, out _); }
        catch (ViewProbe.ReadRefusal ex) { ViewProbe.PluginRead.Report(ex); drc = 2; }   // PluginRead.cs
        if (drc != 0) { Console.Error.WriteLine($"--winning-refs-dump: target {di + 1} of {dumpTargets.Count} ('{dumpTargets[di]}') exited {drc}; stopping."); return drc; }
    }
    Console.WriteLine($"--winning-refs-dump: {dumpTargets.Count} target(s) written to {Path.GetFullPath(winRefsDump)}");
    return 0;
}
int singleRc;
try { singleRc = RunCellTextured(target, 0, 0, null, out _); }
catch (ViewProbe.GlAllocationRefusal ex) { Console.Error.WriteLine(ex.Message); return 2; }   // F-G4-1
catch (ViewProbe.CaptureRefusal ex) { Console.Error.WriteLine(ex.Message); return 2; }        // CaptureViews.cs
catch (ViewProbe.ReadRefusal ex) { ViewProbe.PluginRead.Report(ex); return 2; }   // PluginRead.cs
// FinalExitCode, not a bare return: a shot must never return 3 itself, because the worklist loop
// above stops at the first shot with a non-zero code and a degraded shot must not stop the ones
// after it. The condition accumulates across the process and is spent HERE, once.
if (singleRc >= 0) return ViewProbe.ExtractionScratch.GuardExitCode(ViewProbe.UnreadableAssets.FinalExitCode(singleRc));


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
if (highlightSpecs.Count > 0) { try { hi = FormKey.Factory(highlightSpecs[0]); } catch { } }

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
}
catch (Exception ex) when (__batchFailureBoundary || __reuseFailureBoundary
                           || ViewProbe.GraphicsReuseRed.TestBoundaryEnabled)
{
    // The batch driver and the graphics-cell lease must never surface a Windows application-error
    // dialog. This outer edge includes setup and every using/finally disposal; other invocations
    // (single shots, --interactive, --no-reuse-cell) retain the historical exception behavior.
    Console.Error.WriteLine((__reuseFailureBoundary
        ? "graphics cell reuse unexpected failure:\n"
        : "worklist selection/progress unexpected failure:\n") + ex);
    if (ViewProbe.GraphicsReuseRed.TestBoundaryEnabled)
    {
        try { ViewProbe.GraphicsReuseRed.CaughtUnexpected(ex); }
        catch (Exception logEx) { Console.Error.WriteLine(logEx); }
        _ = ViewProbe.GraphicsReuseRed.Finish();
    }
    return 2;
}
