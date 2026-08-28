using System.Diagnostics;
using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
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

// Defaults are the live VR instance; --instance / --game / --profile point the render at another
// MO2 layout (the SE conflict lab), so a field-test shot shows the exact state the tester will see.
string Instance = @"C:\Games\Skyrim VR\PiranhaRim VR";
string GamePath = @"C:\games\steam\steamapps\common\SkyrimVR";
string profileName = "PiranhaRimVR";
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--instance" && i + 1 < args.Length) Instance = args[i + 1];
    else if (args[i] == "--game" && i + 1 < args.Length) GamePath = args[i + 1];
    else if (args[i] == "--profile" && i + 1 < args.Length) profileName = args[i + 1];
}
string profileDir = Path.Combine(Instance, "profiles", profileName);
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

if (args.Length > 0 && args[0] == "--smoke") return Smoke();
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: viewprobe <CellEditorID|FormID> [--out DIR] [--size N] [--angles 0,90,180] [--elev 25] [--no-nav] [--ref FORMID]");
    return 1;
}

string target = args[0];
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
bool keepFx = false;
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
for (int i = 1; i < args.Length; i++)
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
    else if (args[i] == "--nav" && i + 1 < args.Length) { navMode = args[++i].ToLowerInvariant(); navModeExplicit = true; }
    else if (args[i] == "--conflicts" && i + 1 < args.Length) conflictsPath = args[++i];
    else if (args[i] == "--red" && i + 1 < args.Length) redSpecs.Add(args[++i]);
    else if (args[i] == "--nav-plugin" && i + 1 < args.Length) navPlugin = args[++i];
    else if (args[i] == "--compare" && i + 1 < args.Length)
    { var p = args[++i].Split(','); if (p.Length == 2) compare = (p[0].Trim(), p[1].Trim()); }
    else if (args[i] == "--no-terrain") drawTerrain = false;
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
    else if (args[i] == "--no-nav-diff") navDiff = false;
    else if (args[i] == "--nav-at" && i + 1 < args.Length)
    { var p = args[++i].Split(','); if (p.Length == 2) navAt.Add((float.Parse(p[0]), float.Parse(p[1]))); }
    else if (args[i] == "--refs-at" && i + 1 < args.Length)
    { var p = args[++i].Split(','); if (p.Length >= 2) refsAt.Add((float.Parse(p[0]), float.Parse(p[1]), p.Length > 2 ? float.Parse(p[2]) : 256f)); }
    else if (args[i] == "--nav-color" && i + 1 < args.Length) navColor = args[++i].ToLowerInvariant();
    else if (args[i] == "--markers") showMarkers = true;
    else if (args[i] == "--list-meshes") listMeshes = true;
    else if (args[i] is "--instance" or "--game" or "--profile") i++;   // consumed above
}
if (!drawNav) navMode = "off";
if ((conflictsPath is not null || redSpecs.Count > 0) && !navModeExplicit) navMode = "wire";
if (navMode is not ("off" or "filled" or "wire" or "both"))
{ Console.Error.WriteLine($"--nav '{navMode}' is not one of: off, filled, wire, both."); return 1; }
Directory.CreateDirectory(outDir);

// ---------------------------------------------------------------------------------------
// 1. Resolve the cell's winning state
// ---------------------------------------------------------------------------------------
var sw = Stopwatch.StartNew();
var comp = Mo2LoadOrder.ReadComposition(profileDir);
var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
var order = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir);

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
FormKey? cellKey = null;
string cellLabel = target;
bool interior = false;

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
            bool collectedModel = false;
            if (rec is IModeledGetter mo && mo.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
            {
                var smr = (ISkyrimMajorRecordGetter)rec;
                baseMesh[smr.FormKey] = f.GivenPath;
                collectedModel = true;
                // Editor-marker classification, from evidence the record itself carries: the
                // Is-Marker header flag (0x00800000), a marker-named EditorID, or a model named
                // *marker* / under the markers folder. The game renders none of these.
                bool marker = (smr.MajorRecordFlagsRaw & 0x00800000) != 0
                    || (smr.EditorID?.Contains("marker", StringComparison.OrdinalIgnoreCase) ?? false)
                    || f.GivenPath.Contains("marker", StringComparison.OrdinalIgnoreCase);
                if (marker) baseIsMarker.Add(smr.FormKey);
                if (!string.IsNullOrWhiteSpace(smr.EditorID)) baseEditorId[smr.FormKey] = smr.EditorID!;
            }
            _ = collectedModel;
        }

        foreach (var cp in PlacementExtract.FromMod(m))
        {
            // Worldspace PERSISTENT refs (doors above all). Not detected by the holding cell —
            // the persistent cell masquerades as grid (0,0) — but by each ref's own Persistent
            // flag: the game loads these with the worldspace, so membership in a rendered cell
            // is a question of POSITION, resolved after the target is known.
            if (cp.Worldspace != FormKey.Null)
                foreach (var r in cp.Refs)
                    if (r.Persistent) persistentWin[r.FormKey] = (cp.Worldspace, r);   // later plugin wins
            if (doorWatchKeys.Count > 0)
                foreach (var r in cp.Refs)
                    if (doorWatchKeys.Contains(r.FormKey))
                        { doorWhere[r.FormKey] = (cp.Cell, cp.CellEditorId, r);     // later plugin wins
                          if (Environment.GetEnvironmentVariable("VP_DOOR_DEBUG") == "1")
                              Console.WriteLine($"   [debug] door {r.FormKey} in cp: Cell={cp.Cell} Grid={(cp.Grid is { } gg ? $"{gg.X},{gg.Y}" : "null")} Interior={cp.Interior} Ws={cp.Worldspace} plugin={m.ModKey}"); }
            bool match = cellKey is { } ck ? cp.Cell == ck
                : string.Equals(cp.CellEditorId, target, StringComparison.OrdinalIgnoreCase)
                  || cp.Cell.ToString().Equals(target, StringComparison.OrdinalIgnoreCase)
                  || cp.Cell.ID.ToString("X6").Equals(target.TrimStart('0'), StringComparison.OrdinalIgnoreCase);
            if (!match) continue;

            cellKey ??= cp.Cell;
            cellLabel = cp.CellEditorId ?? cp.Cell.ToString();
            interior = cp.Interior;
            targetWorldspace = cp.Worldspace;
            targetGrid = cp.Grid;
            foreach (var r in cp.Refs) winRef[r.FormKey] = r;
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
    }
}

if (cellKey is null) { Console.Error.WriteLine($"cell '{target}' not found in the active load order."); return 1; }
Console.WriteLine($"cell {cellLabel} {(interior ? "(interior)" : "(exterior)")}  {cellKey}");
if (!interior && targetGrid is { } tgp)
{
    float px0 = tgp.X * 4096f, py0 = tgp.Y * 4096f;
    int addedPersist = 0;
    foreach (var (pws, pr) in persistentWin.Values)
        if (pws == targetWorldspace
            && pr.Position.X >= px0 && pr.Position.X < px0 + 4096f
            && pr.Position.Y >= py0 && pr.Position.Y < py0 + 4096f
            && !winRef.ContainsKey(pr.FormKey))
        { winRef[pr.FormKey] = pr; addedPersist++; }
    if (addedPersist > 0)
        Console.WriteLine($"   +{addedPersist} persistent worldspace ref(s) inside this cell's bounds (doors live here)");
}
Console.WriteLine($"winning state: {winRef.Count:N0} refs, {winNav.Count:N0} navmeshes  ({sw.Elapsed.TotalSeconds:0.0}s)");

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
        foreach (var dfk in doorWatchKeys)
            if (!winRef.ContainsKey(dfk))
                Console.WriteLine(doorWhere.TryGetValue(dfk, out var dw)
                    ? $"   implicated door {dfk} is NOT in this cell: it sits in {dw.CellEd ?? dw.Cell.ToString()} at {dw.Ref.Position.X:0},{dw.Ref.Position.Y:0},{dw.Ref.Position.Z:0}"
                    : $"   implicated door {dfk}: no placed ref with this FormKey anywhere in the load order");
        if (redHere == 0 && conflictDoors.Count == 0)
            Console.WriteLine($"   (no conflict in that file names a navmesh or door in THIS cell — nothing to paint)");
    }

    var scene = new List<ViewProbe.SceneRef>();
    int fxSkipped = 0, markerSkipped = 0;
    var meshCensus = listMeshes ? new Dictionary<string, (int Count, string Class)>(StringComparer.OrdinalIgnoreCase) : null;
    foreach (var r in winRef.Values)
    {
        if (r.InitiallyDisabled || r.Deleted) continue;
        if (r.Base is not { } bk || !baseMesh.TryGetValue(bk, out var rel)) continue;
        // Effect meshes (beams, mists, glows) are translucent volumes that drown a first-person
        // shot; the game draws them subtly, this flat-lit pass cannot. Skipped by default,
        // counted, and --fx puts them back.
        bool isMarker = baseIsMarker.Contains(bk);
        bool isFx = rel.Replace('/', '\\').Contains(@"effects\", StringComparison.OrdinalIgnoreCase);
        if (meshCensus is not null)
        {
            var cls = isMarker ? "MARKER" : isFx ? "FX" : "geometry";
            meshCensus[rel] = (meshCensus.TryGetValue(rel, out var c) ? c.Count + 1 : 1, cls);
        }
        if (!keepFx && isFx) { fxSkipped++; continue; }
        // Editor markers are not world geometry; the game never draws them. --markers keeps them
        // as a translucent extra layer instead of dropping them.
        if (isMarker && !showMarkers) { markerSkipped++; continue; }
        scene.Add(new ViewProbe.SceneRef(rel, r.Position, r.Rotation, r.Scale,
                                         (hiFp is { } h && r.FormKey == h) || conflictDoors.Contains(r.FormKey), isMarker));
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
                       : r.InitiallyDisabled ? "NOT DRAWN: initially disabled"
                       : r.Base is not { } bk0 ? "NOT DRAWN: no base"
                       : !baseMesh.TryGetValue(bk0, out _) ? "NOT DRAWN: base carries no model"
                       : baseIsMarker.Contains(bk0) && !showMarkers ? "not drawn: editor marker (--markers)"
                       : baseMesh[bk0].Replace('/', (char)92).Contains("effects" + (char)92, StringComparison.OrdinalIgnoreCase) && !keepFx ? "not drawn: FX (--fx)"
                       : "drawn";
            string bse = r.Base is { } bk1
                ? bk1 + (baseEditorId.TryGetValue(bk1, out var be) ? $" {be}" : "")
                  + (baseMesh.TryGetValue(bk1, out var bm) ? $"  {bm}" : "")
                : "(none)";
            Console.WriteLine($"      {d,6:0} u  {r.FormKey}  at {r.Position.X:0},{r.Position.Y:0},{r.Position.Z:0}"
                            + $"  scale {r.Scale:0.00}  [{why}]  base {bse}");
        }
    }

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
    if (drawTerrain && !interior && targetGrid is { } tg)
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

    string? DoorCaption()
        => conflictDoors.Count == 0 ? null
         : "ORANGE door " + string.Join("   ", conflictDoors.Select(fk => $"{fk} = FormID {RuntimeId(fk)}"));

    string RedCaption(IReadOnlyDictionary<FormKey, NavmeshGeometry> navs)
        => string.Join("   ", redNav.Keys.Select(fk =>
               $"RED {fk} = FormID {RuntimeId(fk)}" +
               (navs.TryGetValue(fk, out var nv) ? $" ({nv.Triangles.Count} tris)" : " (not provided)")));

    var fpNws = new NativeWindowSettings
    {
        ClientSize = new OpenTK.Mathematics.Vector2i(size, size),
        StartVisible = false,
        StartFocused = false,
        WindowBorder = WindowBorder.Hidden,
        APIVersion = new Version(3, 3),
        Profile = ContextProfile.Core,
    };
    using var fpGw = new GameWindow(GameWindowSettings.Default, fpNws);
    fpGw.MakeCurrent();

    string safe = new string(cellLabel.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    // The target cell's own footprint, for the outline. Exteriors only — an interior has no grid
    // and no neighbours to be confused with.
    (float X0, float Y0, float X1, float Y1)? cellBounds = null;
    if (cellOutline && !interior && targetGrid is { } tgb)
        cellBounds = (tgb.X * 4096f, tgb.Y * 4096f, tgb.X * 4096f + 4096f, tgb.Y * 4096f + 4096f);

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
               skyColor, terrainHeightAt, dashUnderground, dashUnderStatics, msaa, cellBounds,
               diffs ?? noDiff, diffColor, picks, captionLines, size, outFile);

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
            var cap = new List<string> { $"[{pi + 1}/3] {(pi == 2 ? "winning state (actual merged navmesh)" : $"cell navmesh if {label} won")} \u2014 {cellLabel}" };
            if (redNav.Count > 0) cap.Add(RedCaption(navs));
            if (DoorCaption() is { } dc) cap.Add(dc);
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
            canvas.SaveAsPng(stitched);
        }
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

    string fpOut = Path.Combine(outDir, $"{safe}-fp.png");
    var fpCap = new List<string> { $"{cellLabel} \u2014 {(navPlugin ?? "winning state")} \u2014 eye {eyePos.Value.X:0},{eyePos.Value.Y:0},{eyePos.Value.Z:0}" };
    if (redNav.Count > 0) fpCap.Add(RedCaption(navPlugin is not null && navByPlugin.TryGetValue(navPlugin, out var np0) ? np0 : winNav));
    if (DoorCaption() is { } fdc) fpCap.Add(fdc);
    int drawnShapes = Render(navTris, redTris, fpOut, fpCap);
    Console.WriteLine($"wrote:\n   {fpOut}");
    Console.WriteLine($"   eye {eyePos.Value.X:0},{eyePos.Value.Y:0},{eyePos.Value.Z:0} -> look {lookPos.Value.X:0},{lookPos.Value.Y:0},{lookPos.Value.Z:0}  fov {fov:0}°  ({drawnShapes:N0} shapes)");
    return 0;
}

// ---------------------------------------------------------------------------------------
// 2. Build world geometry
// ---------------------------------------------------------------------------------------
var verts = new List<float>();      // 18 floats per vertex — GlMesh.Upload's exact layout
var idx = new List<int>();
var navVerts = new List<float>();
var navIdx = new List<int>();
var lineVerts = new List<float>();

var geoCache = new Dictionary<string, TriangleSoup?>(StringComparer.OrdinalIgnoreCase);
int drawn = 0, noGeo = 0;
long triCount = 0;
var bounds = Aabb.Empty;

FormKey? hi = null;
if (highlight is not null) { try { hi = FormKey.Factory(highlight); } catch { } }

var activeOrder = SpatialOverlap.EulerOrder.Xyz;

void Build(SpatialOverlap.EulerOrder order)
{
    activeOrder = order;
    verts.Clear(); idx.Clear(); navVerts.Clear(); navIdx.Clear(); lineVerts.Clear();
    drawn = 0; noGeo = 0; triCount = 0; bounds = Aabb.Empty;

    sw.Restart();
    foreach (var r in winRef.Values)
    {
        if (r.InitiallyDisabled || r.Deleted) continue;
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

Console.WriteLine($"geometry: {drawn:N0} refs drawn ({noGeo:N0} without usable mesh), {triCount:N0} triangles, "
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
