using System.Numerics;
using CharacterViewer.Rendering;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using OpenTK.Graphics.OpenGL4;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.Fonts;
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Image = SixLabors.ImageSharp.Image;
using GlVec3 = OpenTK.Mathematics.Vector3;
using GlVec4 = OpenTK.Mathematics.Vector4;

namespace ViewProbe;

internal sealed record NavColourGroup(string Colour, IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> Triangles);

// TEXTURED, FIRST-PERSON RENDERING — "see what the tester will see".
//
// This is the charter's documented upgrade executed as documented: CharacterViewer.Rendering
// already loads NIFs with their texture sets and draws them (it is where viewprobe's vertex
// layout came from), so this file WIRES rather than reinvents:
//
//   GameAssetResolver   <- IDataFolderProvider (game Data) + IBsaArchiveProvider (a thin shim
//                          over houseCARL's AssetResolver/TryReadArchiveEntry) + the profile's
//                          mod folders via SetAdditionalFolders — MO2-style last-wins.
//   NifMeshBuilder      <- BuildFromFile per distinct mesh path; it applies the NIF's own node
//                          chain (GetTransformToGlobal) and emits Y-up geometry. For placed
//                          objects viewprobe then removes the root node's transform: Skyrim
//                          starts REFR placement below that root (proved against the game by
//                          SHouseIntDoor02's -90-degree root and 270-degree REFR heading).
//   GlTextureManager    <- LoadTexture(relative path) resolves + decodes DDS, caches, falls
//                          back to a white texture rather than failing a shape.
//   GlRenderer          <- AddMesh + Render(OrbitCamera). NPC-only features stay off.
//
// COORDINATES. The library is Y-up: yUp = (x, z, -y) of Skyrim's Z-up (the exact swap
// NifMeshBuilder uses). Placement transforms are composed in SKYRIM space with the SAME Euler
// convention the detectors use (SpatialOverlap.Euler), then the finished world-space vertices
// are swapped once. The camera takes Skyrim-space eye/look and swaps the same way, so a tester
// can type coordinates straight off a `getpos` console line.
//
// The per-reference model transform is baked CPU-side (GlMesh carries no model matrix), which
// also matches how the flat path works — same mesh placed N times = N uploads of transformed
// copies, cached at the BuiltMesh level so the NIF is parsed once.

sealed class NullNpcSource : INpcMeshDataSource
{
    public ResolvedNpcMeshPaths? Resolve(NpcIdentity identity) => null;
    public object? CurrentInvalidationToken => this;
}

sealed class HcDataFolder(string dataDir) : IDataFolderProvider
{
    public string DataFolderPath => dataDir;
    public object? CurrentLoadOrderToken => this;
}

/// <summary>houseCARL's VFS answering the library's BSA questions. Locate = "which archive wins
/// this path in the ACTIVE order"; extract = houseCARL's own single-entry reader. The scoped
/// variant is never exercised here (we use AdditionalFolders, not AdditionalScopes) and says so
/// rather than guessing.</summary>
sealed class HcBsaProvider(AssetResolver assets) : IBsaArchiveProvider
{
    public void EnsureAllArchivesOpened() { }

    public bool TryLocateInBsa(string subpath, out string? containingBsaPath)
    {
        containingBsaPath = null;
        var res = assets.ResolveForPlacement(subpath);
        foreach (var s in res.Sources)
            if (s.ArchivePath is { Length: > 0 } bsa) { containingBsaPath = bsa; return true; }
        return false;
    }

    public bool TryLocateInScopedBsa(string subpath, string folderPath, IReadOnlyList<string> modKeyFileNames, out string? containingBsaPath)
    { containingBsaPath = null; return false; }

    public bool TryExtractToDisk(string containingBsaPath, string subpath, string destPath, out string? error)
    {
        // THE GUARD, and deliberately OUTSIDE the try below: a refusal that became an `error` string
        // would be a refusal the library swallows into a white fallback. Every destination the
        // library picks passes through here, so if it ever stops taking that destination from
        // AppContext.BaseDirectory this run stops loudly on the first extraction instead of quietly
        // returning to the folder beside the exe that every process shares.
        ExtractionScratch.RequireInside(destPath);
        try
        {
            var bytes = AssetResolver.TryReadArchiveEntry(containingBsaPath, subpath);
            if (bytes is null) { error = $"'{subpath}' not present in '{Path.GetFileName(containingBsaPath)}'"; return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.WriteAllBytes(destPath, bytes);
            error = null;
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}

sealed class ConsoleCvLogger : ICharacterViewerLogger
{
    public void LogMessage(string message) { if (Environment.GetEnvironmentVariable("VP_CV_VERBOSE") == "1") Console.Error.WriteLine($"   [cv] {message}"); }
    public void LogError(string message) => Console.Error.WriteLine($"   [cv] {message}");
    public void LogError(string message, Exception ex) => Console.Error.WriteLine($"   [cv] {message}: {ex.Message}");
}

sealed class CvSettings : ICharacterViewerSettings
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public string CharacterViewerLightingLayout { get; set; } = "";
    public string CharacterViewerLightingColorScheme { get; set; } = "";
    public IList<CharacterViewerLightingLayout> UserLightingLayouts { get; } = new List<CharacterViewerLightingLayout>();
    public IList<CharacterViewerLightingColorScheme> UserLightingColorSchemes { get; } = new List<CharacterViewerLightingColorScheme>();
    public bool CharacterViewerVerboseLog { get; set; } = false;
}


/// <summary>One placed instance handed over from Program.cs: the mesh to draw, the Skyrim-space
/// placement to draw it at, and (for the interactive picker's reports) who it is — the placed
/// ref's own FormKey, its base, and the base's EditorID. Under the Base Object Swapper layer
/// <paramref name="BaseKey"/> and the placement are the EFFECTIVE ones (what the game loads);
/// <paramref name="BosRecordBase"/> keeps the record's base for a swapped ref and
/// <paramref name="BosLabel"/> the one-line account for the hover and the picker.</summary>
readonly record struct SceneRef(string MeshRelPath, Vec3 Position, Vec3 Rotation, float Scale, bool Highlight,
                                bool Marker = false, FormKey RefKey = default, FormKey BaseKey = default,
                                string? BaseEditorId = null, FormKey? BosRecordBase = null, string? BosLabel = null,
                                bool BosRandom = false, int StateChange = 0, string? StateLabel = null,
                                bool Constructed = false, Vector3 HighlightColor = default)
{
    public bool BosSwapped => BosRecordBase is not null;
    /// <summary>--state: +1 the flip brings this ref in, −1 it takes it out (drawn only in the diff view), 0 unchanged.</summary>
    public bool StateArrives => StateChange > 0;
    public bool StateDeparts => StateChange < 0;
}

/// <summary>A PROJECTED COVERING — the engine's MATO (MaterialObject) layer, which is what puts the
/// snow on Winterhold, the ash on Solstheim and the moss on Markarth. It is NOT an effect shader,
/// so no amount of <c>--fx</c> ever produced it; the renderer simply did not read the record.
///
/// The wiring is narrow and worth stating exactly, because it bounds the feature: a MATO reaches
/// the world through ONE field on ONE record type — <c>STAT.Material</c>, with <c>STAT.MaxAngle</c>
/// beside it (verified by construction against the whole modeled record library: Static is the only
/// type carrying a <c>FormLink&lt;IMaterialObjectGetter&gt;</c>). So coverings land on statics and
/// nothing else — no clutter, no furniture, no actors — and a bare patch on a non-STAT is correct,
/// not a gap.
///
/// COVERAGE MODEL. <see cref="ProjectionVector"/> is the direction the material travels (snow falls,
/// so vanilla snow is <c>0,0,-1</c>); a face catches it in proportion to how squarely it faces back
/// into it, <c>d = dot(faceNormal, -projection)</c>. <see cref="MaxAngleDeg"/> is the hard outer
/// gate from the STAT. The BIAS/SCALE ramp is FITTED, not engine-exact — the shape was read off the
/// data's own spread rather than guessed: vanilla snow is bias 0.40, and the deliberately-thinner
/// variants climb monotonically (Windhelm's "Light" 0.92, "EvenLess" 0.94, "Minimal" 0.98), so a
/// HIGHER bias means LESS coverage and bias is a threshold on d with scale as the ramp width.
/// Say so in the docs rather than implying the curve is the engine's.</summary>
readonly record struct Covering(Vector3 ProjectionVector,     // Skyrim space, direction of travel
                                float MaxAngleDeg,            // STAT.MaxAngle — the hard gate
                                float FalloffBias, float FalloffScale,
                                float UvScale,                // MATO.MaterialUvScale, world u per tile
                                Vector4 Color,                // SinglePassColor, or white when a texture carries it
                                string? ModelPath,            // the MATO's own model NIF; its slot-0 diffuse IS the covering texture
                                string Label);                // MATO EditorID, for the census

/// <summary>WATER over one loaded cell. Drawn because its ABSENCE was the bug: with nothing at the
/// water height, sea reads as sky, and this project's standing rule is that empty background is a
/// defect signal — the Winterhold shot showed open blue where the game shows the Sea of Ghosts.
///
/// Height resolution follows the engine: <c>CELL.WaterHeight</c> when the cell sets one, and the
/// worldspace's <c>LandDefaults.DefaultWaterHeight</c> when it holds the <c>3.4028235E+38</c>
/// sentinel (which Winterhold's own cell does — Tamriel's default is −14000). Colour is the WATR
/// record's, blended shallow→deep by the real depth to the LAND surface underneath, so a shoreline
/// reads as a shoreline from above.
///
/// The NOISE TEXTURE IS BOUND AS A NORMAL MAP, and that is a schema fact rather than a guess about
/// the art: WATR carries no diffuse texture field at all — only <c>NoiseLayerOne/Two/ThreeTexture</c>
/// and <c>FlowNormalsNoiseTexture</c> — so the colour has to come from the record's own colours and
/// the noise layers are wave normals. Binding one as a diffuse would repaint the sea in normal-map
/// lilac, which is a bug this renderer has already been bitten by once.
///
/// What is NOT modelled, and should not be read into the picture: no reflection or refraction
/// buffer, no flow, no specular sun sparkle. The sky term is a flat <c>WaterReflectivity</c> mix,
/// not a Fresnel.</summary>
readonly record struct WaterPlane(float Z,                                        // Skyrim z of the surface
                                  float X0, float Y0, float X1, float Y1,         // cell footprint
                                  string? NoiseTexture, float NoiseUvScale,
                                  Vector3 ShallowColor, Vector3 DeepColor,        // 0..1
                                  Vector3 ReflectionColor,                        // 0..1, WATR's own
                                  float Reflectivity, float Opacity,              // 0..1
                                  float Fresnel,                                  // WATR.WaterFresnel, used as Schlick F0
                                  string Label);

static class TexturedScene
{
    internal static int ReportModeSmoke(string outDir)
    {
        Directory.CreateDirectory(outDir);
        const int w = 320, h = 180;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        { int p = (y * w + x) * 4; pixels[p] = (byte)(x % 251); pixels[p + 1] = (byte)(y % 251); pixels[p + 2] = 90; pixels[p + 3] = 255; }
        var normal = Path.Combine(outDir, "captioned.png"); var clean = Path.Combine(outDir, "report.png");
        var captions = new[] { "CAPTION_SENTINEL" };
        var coloured = new[] { ("COLOUR_SENTINEL", new Vector3(0, 1, 1)) };
        var pinned = new[] { ("GRID_SENTINEL", 20, 100) };
        SaveFrame((byte[])pixels.Clone(), w, h, captions, coloured, "pose: identical", normal,
            pinned, new Vector3(1, 1, 0));
        SaveFrame((byte[])pixels.Clone(), w, h, captions, coloured, "pose: identical", clean,
            pinned, new Vector3(1, 1, 0), reportMode: true);
        using var expected = Image.LoadPixelData<Rgba32>((byte[])pixels.Clone(), w, h);
        expected.Mutate(x => x.Flip(FlipMode.Vertical));
        using var a = Image.Load<Rgba32>(normal); using var b = Image.Load<Rgba32>(clean);
        bool differ = false, rawSceneIdentical = true;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        { if (a[x, y] != b[x, y]) differ = true; if (b[x, y] != expected[x, y]) rawSceneIdentical = false; }
        bool meta = b.Metadata.GetPngMetadata().TextData.Any(t => t.Keyword == "viewprobe" && t.Value == "pose: identical");
        Console.WriteLine($"report-mode-smoke: textPixelsRemoved={differ} rawSceneIdentical={rawSceneIdentical} metadataPreserved={meta}");
        return differ && rawSceneIdentical && meta ? 0 : 1;
    }

    // Z-up (Skyrim) -> Y-up (viewer): the exact swap NifMeshBuilder uses.
    internal static Vector3 YUp(Vector3 z) => new(z.X, z.Z, -z.Y);

    /// <summary>Normalize, or Vector3.Zero for a degenerate input — so a bad normal cannot become
    /// NaN and poison a whole vertex buffer.</summary>
    internal static Vector3 Unit(Vector3 d) => d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.Zero;

    /// <summary>How far a projected covering shell sits off its base surface, in Skyrim units.
    /// IsDecal keeps it out of the depth buffer but does not offset it, and exactly-coplanar
    /// geometry stipples at any real camera range. ~3 cm: invisible, and far above the depth
    /// buffer's resolution at these distances.</summary>
    const float CoveringLift = 2f;

    /// <summary>A walking route drawn over the scene: straight segments between waypoints, with
    /// arrowheads riding them that say which way to go. Everything here lies FLAT ON THE GROUND —
    /// the line is painted on the floor, not billboarded at the camera — because the route follows
    /// the navmesh's own height and has to read as part of the ground the tester walks on.</summary>
    /// <param name="Legs">
    /// One entry per AUTHORED segment (waypoint i to waypoint i+1), each already densified by the
    /// caller into a polyline that FOLLOWS THE GROUND: straight in plan, riding the navmesh in z.
    /// Straight in 3D would be wrong — a chord from the foot of an arched walkway to its crest
    /// passes ~50 u UNDER the walkway, and with waypoints placed only where the direction changes
    /// there is nothing to hide that. The renderer never guesses a height; every z here was
    /// resolved from the mesh.
    /// </param>
    /// <param name="Waypoints">The AUTHORED vertices only — what the discs mark. A leg's interior
    /// points are ground samples and are not waypoints; marking them would put a disc every 32 u.</param>
    /// <param name="Color">Reserved, and excluded from the nav wire's auto-pick — see the pick below.</param>
    /// <param name="Discs">Draw a translucent fill disc at each waypoint.</param>
    /// <param name="ArrowEvery">Units between arrowheads: null = distance-scaled so the spacing is
    /// constant ON SCREEN, 0 = exactly one at the end of each leg, otherwise this many units.</param>
    internal sealed record RouteRender(
        IReadOnlyList<IReadOnlyList<Vector3>> Legs,
        IReadOnlyList<Vector3> Waypoints,
        Vector4 Color,
        bool Discs,
        float? ArrowEvery);

    /// <summary>--grid: a labelled world-coordinate grid ruled across the ground plane of a
    /// top-down, so a position is READ off the picture instead of derived from the caption pose.
    /// That derivation is arithmetic done by eye against a screenshot, and it is where silent
    /// errors come from — a route planned from a mis-scaled pixel measurement looks exactly like
    /// one planned from a correct one.</summary>
    /// <param name="Spacing">World units between lines, or null for auto — the renderer then picks
    /// a 1-2-5 round number putting roughly 9 lines across the frame, which is what makes the
    /// labels readable rather than a hatch.</param>
    /// <param name="Z">The Skyrim height the grid is ruled AT. It matters: the grid is a plane in
    /// the world, so a plane at the wrong storey projects to the wrong pixels and the numbers read
    /// off it are wrong by exactly the parallax the flag exists to remove.</param>
    /// <param name="Color">Reserved, and excluded from the nav wire's auto-pick the same way the
    /// route's colour is — a grid sharing the navmesh's hue makes the whole overlay unreadable.</param>
    /// <param name="ZWhy">Where <paramref name="Z"/> came from, for the console line. An assumed
    /// height has to say it is assumed.</param>
    internal sealed record GridRender(float? Spacing, float Z, Vector4 Color, string ZWhy);
    /// <summary>A ribbon the caller draws on the ground in its own colour — the landscape-seam overlay (--seams, 2026-09-04)
    /// and anything after it. Skyrim-space points; <paramref name="Width"/> scales the ribbon's distance law (1 = the grid's width ×1.4).</summary>
    internal readonly record struct OverlayLine(IReadOnlyList<Vector3> Pts, Vector3 Colour, float Width);

    /// <summary>MO2-style loose-file precedence, the ONE encoding of the
    /// convention: the resolver iterates LAST-to-first, so hand it
    /// lowest-priority-first with overwrite at the very end (it outranks every
    /// mod). Shared by this scene's resolver and the NPC actor host's.</summary>
    internal static List<string> ResolverFolders(IReadOnlyList<string> modFoldersHighestFirst, string overwriteDir)
    {
        var folders = new List<string>();
        for (int i = modFoldersHighestFirst.Count - 1; i >= 0; i--) folders.Add(modFoldersHighestFirst[i]);
        if (!string.IsNullOrWhiteSpace(overwriteDir) && Directory.Exists(overwriteDir)) folders.Add(overwriteDir);
        return folders;
    }

    /// <summary>Build, render and save. The GL context must already be current (Program.cs owns the
    /// hidden GameWindow). Returns the number of shapes drawn.</summary>
    public static int RenderToPng(
        IReadOnlyList<SceneRef> refs,
        AssetResolver assets,
        string dataDir,
        IReadOnlyList<string> modFoldersHighestFirst,
        string overwriteDir,
        (Vector3 Eye, Vector3 Look, float FovDeg) cam,     // Skyrim Z-up coordinates
        IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> navTris,
        IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> redTris,   // the FINDING, always painted
        IReadOnlyDictionary<string, List<(Vector3 A, Vector3 B, Vector3 C)>> terrainPatches, // diffuse path ("" = untextured) -> tris
        IReadOnlyList<(string Diffuse, List<(Vector3 A, Vector3 B, Vector3 C, float Oa, float Ob, float Oc)> Tris)> terrainLayers,
        string navMode,                                    // off | filled | wire | both
        string navColor,                                   // auto | green | cyan | magenta | yellow | blue
        Vector3 skyColor,                                  // what the frame clears to (0..1 RGB)
        Func<float, float, float?>? terrainHeightAt,       // winning LAND surface Z at a world X,Y
        bool dashUnderground,                              // dash navmesh buried under the terrain
        bool dashUnderStatics,                             // dash navmesh hidden behind geometry
        int msaa,                                          // multisample count; < 2 = off
        float? cullAbove,                                  // dollhouse slice: drop static triangles wholly above this Skyrim z
        (float X0, float Y0, float X1, float Y1)? cellBounds, // target cell footprint; null = no outline
        IReadOnlyList<Vector3> diffVerts,                  // navmesh vertices unique to THIS panel
        Vector4 diffColor,                                 // colour for those vertices' spheres
        IReadOnlyList<(int X, int Y)> picks,                // image pixels to report world coords for
        IReadOnlyList<string> captionLines,                // burned into the image, top-left (white)
        IReadOnlyList<(string Text, Vector3 Color)> colourCaptionLines, // coloured caption lines (actors, route), drawn AFTER the white ones
        IReadOnlyList<NpcActorRender> npcActors,           // placed characters — meshes BORROWED (see below), never disposed here
        bool npcPucks,                                     // per-actor-coloured ground disc + always-visible rim under each actor
        IReadOnlyList<RouteRender> routes,                 // the paths drawn on the ground, each in its own colour: segments + arrowheads; empty = none
        GridRender? grid,                                  // labelled world-coordinate grid on the ground plane; null = none
        IReadOnlyList<OverlayLine> overlayLines,           // --seams and any other caller-drawn ribbon on the ground; empty = none
        bool keepFx,                                       // --fx: draw effect-shader shapes too
        bool fxStatic,                                     // --fx-static: draw them as a frozen frame
        bool listEffectMeshes,                             // --list-meshes: name the meshes they came from
        IReadOnlyList<WaterPlane> waterPlanes,             // one per loaded cell that has water; empty = --no-water or none
        IReadOnlyDictionary<FormKey, Covering> coveringsByBase,  // STAT base -> its MATO layer; empty = --no-coverings
        IReadOnlyList<PlaneRender> planes,                 // every occlusion plane in the cell (+ ring); see OcclusionPlanes.cs
        bool showPlanes,                                   // --planes: draw the planes layer
        string occlusionMode,                              // --occlusion off | on | diff: preview what the planes cull
        int sizeW, string outPath,
        bool reportMode = false,                           // omit all raster text; preserve pixels, overlays and metadata
        string? pngTextMeta = null,                        // replication chunk: written as a PNG tEXt entry keyed "viewprobe"
        InteractiveOptions? interactive = null,            // non-null: open the fly-through window instead of saving a PNG
        Action<OcclusionState>? planeReport = null,        // --planes-report: handed the planes' cull answer for the entry eye
        string bosMode = "on",                             // --bos off | on | diff: the Base Object Swapper layer's paint (BosView.cs)
        string stateMode = "on",                           // --state-view on | diff: the world state's paint (StateView.cs)
        string stateName = "",                             // --state: the state's name for the caption and the window
        int visibilityBudget = OcclusionState.DefaultVisibilityBudget,
        IReadOnlyList<NavColourGroup>? navColourGroups = null,
        bool refSurface = false,
        float refSurfaceOpacity = 1f,                   // --ref-surface-opacity, validated to (0,1] by the CLI; 1 is the shipped opaque mark
        string? depthDumpPath = null,                   // --depth-dump: write the finished frame's window depth (the array --pick reads) here, verbatim
        int sizeHeight = 0)                             // capture canvas HEIGHT in pixels; 0 keeps the historic square (sizeW x sizeW)
    {
        // THE CANVAS IS TWO NUMBERS, NOT ONE. It used to be `size` on both axes, and the camera's
        // fov went to the projection at aspect 1 -- so a pose planned for a 16:9 deliverable was
        // captured as a square, which keeps the VERTICAL angle and crops roughly a third off the
        // horizontal. Nothing failed: the frame rendered, the subjects were still in it, and the
        // planner's framing numbers (fill, centring) silently stopped describing the delivered
        // picture (Piranha, 2026-09-14). `sizeW` is the width; height defaults to it, so every
        // existing caller keeps the exact square frame it had.
        int sizeH = sizeHeight > 0 ? sizeHeight : sizeW;
        // The projection's aspect comes from the canvas that will actually be read back, so the
        // delivered pixels and the --pick unprojection cannot disagree about the frustum.
        float canvasAspect = (float)sizeW / sizeH;
        var logger = new ConsoleCvLogger();
        var gate = new CharacterViewerLogGate { Verbose = Environment.GetEnvironmentVariable("VP_CV_VERBOSE") == "1" };

        var folders = ResolverFolders(modFoldersHighestFirst, overwriteDir);

        // The bracket. GameAssetResolver captures Path.Combine(AppContext.BaseDirectory,
        // "CharacterViewerCache") in its constructor and never re-reads it, so this is the one line
        // in the run where the redirect has to be in force — see ExtractionScratch.cs.
        GameAssetResolver resolver;
        using (ExtractionScratch.Redirect())
            resolver = new GameAssetResolver(new HcDataFolder(dataDir), new HcBsaProvider(assets), gate, logger);
        resolver.SetAdditionalFolders(folders);

        var builder = new NifMeshBuilder(logger, gate, resolver, new CvSettings());
        var cache = new CharacterPreviewCache(new NullNpcSource(), resolver, logger, gate, new CvSettings());
        using var textures = new GlTextureManager(cache, logger);
        textures.Initialize();

        using var renderer = new GlRenderer();
        renderer.Initialize(null);                       // embedded shaders
        renderer.EnableBloom = false;                    // deterministic output; no post FX
        renderer.EnableShadows = false;
        renderer.EnableAmbientOcclusion = false;
        // The sky. GlRenderer.Render() clears the frame AGAIN with its own ClearColor, so this
        // is the setting that decides the background — the GL.ClearColor in Draw() below only
        // matters for the instant before the library's clear lands on top of it.
        renderer.ClearColor = new GlVec3(skyColor.X, skyColor.Y, skyColor.Z);
        renderer.SetAmbientIntensity(0.55f);
        renderer.SetKeyLight(azimuthDeg: 135f, elevationDeg: 55f, intensity01: 0.85f, new GlVec3(1f, 0.98f, 0.92f));
        renderer.SetFillLight(azimuthDeg: 315f, elevationDeg: 20f, intensity01: 0.35f, new GlVec3(0.85f, 0.9f, 1f));

        // A 1x1 orange diffuse for the highlighted ref — the same signal color the flat mode uses.
        int orange = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, orange);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1, 1, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 255, 90, 26, 255 });
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        var builtCache = new Dictionary<string, List<NifMeshBuilder.BuiltMesh>?>(StringComparer.OrdinalIgnoreCase);

        // A MATO names a MODEL, not a texture, so the covering's diffuse is whatever that model's
        // first drawable shape carries in slot 0 (vanilla snow: landscape/snowmatshader.nif). Built
        // through the same builder and cached per path — there are a handful of distinct materials
        // in any scene, against thousands of refs.
        var matoTexCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int MatoTexture(string? modelPath)
        {
            if (modelPath is not { Length: > 0 }) return textures.WhiteTexture;
            if (matoTexCache.TryGetValue(modelPath, out var cached)) return cached;
            int handle = textures.WhiteTexture;
            var mrel = modelPath.StartsWith("meshes", StringComparison.OrdinalIgnoreCase)
                     ? modelPath : Path.Combine("meshes", modelPath);
            try
            {
                var mabs = resolver.ResolveAssetPath(mrel);
                if (mabs is not null)
                    foreach (var mb in builder.BuildFromFile(mabs))
                        if (mb.TexturePaths.TryGetValue(0, out var t0) && t0 is { Length: > 0 })
                        {
                            int h = textures.LoadTexture(t0);
                            if (h != textures.WhiteTexture) { handle = h; break; }
                        }
            }
            catch (Exception ex) { logger.LogError($"MATO model '{mrel}'", ex); }
            matoTexCache[modelPath] = handle;
            return handle;
        }
        int shapes = 0, missingMesh = 0, texturedShapes = 0;
        // Effect-shader shapes dropped this render: the total shape-draws, and how many such
        // shapes each distinct mesh carries (a per-file property, so it is SET, not accumulated,
        // however many refs place that mesh).
        int fxShapesSkipped = 0, nmShapesSkipped = 0, fxRefCount = 0;
        int fxStaticShapes = 0, fxStaticRefCount = 0;
        int cullTrisDropped = 0, cullShapesDropped = 0;
        int coveringShapes = 0, coveringRefs = 0;
        var coveringByMato = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int waterMeshes = 0; long waterTris = 0;
        long waterSubmerged = 0, waterSamples = 0;
        // env VP_FXSTATIC_EMIT — brightness of the --fx-static stand-in. The real number is
        // BSEffectShaderProperty.baseColorScale, which the builder does not read; until it does,
        // this is the knob that stands in for it.
        float fxStaticEmit = float.TryParse(Environment.GetEnvironmentVariable("VP_FXSTATIC_EMIT"),
                                            out var fse) ? fse : 1f;
        var fxByMesh = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // env VP_SHAPE_DUMP=<substr> — the SHAPE-level companion to --list-meshes: for every mesh
        // whose path matches, print what the builder actually read off each shape. --list-meshes
        // says which files are in the cell; this says why one of their shapes draws as it does.
        // Printed once per distinct mesh, at build time, before any skip decision.
        string? shapeDump = Environment.GetEnvironmentVariable("VP_SHAPE_DUMP");
        var shapeDumped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var meshes = new List<GlMesh>();
        // Highlighted refs keep their own texture; their halo (an inflated inverted hull) is
        // emitted here and drawn by the OVERLAY pass, whose zero-normal vertices render as the
        // exact flat colour — the library pass would shade the hull's inward faces to black.
        var haloTris = new List<float>();
        var subjectSurface = new List<float>();
        var subjectSurfaceDoubleSided = new List<float>();
        var subjectSurfaceRefusals = new HashSet<string>();
        // Near faces (outward winding), for the deep-occlusion boost: they pass a GREATER
        // depth test only when the occluder sits in front of the WHOLE hull — a wall, never
        // the object itself or its own door-jamb — so the through-wall ghost gets a second
        // blend layer and pops, while a visible object keeps only the light wash.
        var haloNear = new List<float>();
        var haloOrange = new Vector3(255f / 255f, 90f / 255f, 26f / 255f);

        // The occlusion preview's candidates: every drawn ref's world bounds (of exactly the
        // triangles uploaded, post-cull) and the meshes they became. Gathered whether or not the
        // preview is on — it is a few numbers per ref — and applied only when it is, so a default
        // render is untouched.
        var occl = new OcclusionState(planes) { VisibilityBudget = visibilityBudget };
        bool needOcclusion = planeReport is not null || (planes.Count > 0 && (occlusionMode != "off" || interactive is not null));
        var boundsCache = new Dictionary<string, (NifGeometryModel? Model, string Hash, string? Issue)>(StringComparer.OrdinalIgnoreCase);
        var geometryInputIssues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // The Base Object Swapper layer's paint: the swapped refs' meshes, tinted in `diff` (BosView.cs).
        var bosView = new BosViewState(bosMode);
        // The world state's paint: the refs the flip brought in and took out (StateView.cs).
        var stateView = new StateViewState(stateMode, stateName);

        foreach (var r in refs)
        {
            var ro = new RefOcclusion { Ref = r };
            occl.Refs.Add(ro); // Missing and unsupported assets stay in the audit population.
            var rel = r.MeshRelPath.StartsWith("meshes", StringComparison.OrdinalIgnoreCase)
                ? r.MeshRelPath : Path.Combine("meshes", r.MeshRelPath);
            if (!boundsCache.TryGetValue(rel, out var source))
            {
                var boundPath = resolver.ResolveAssetPath(rel);
                source = (null, "", "model asset missing");
                if (needOcclusion && boundPath is not null)
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(boundPath);
                        source = (null, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), null);
                        var measured = NifGeometry.Measure(bytes);
                        source = (measured.Model, source.Hash,
                                  measured.Model is null ? "NIF geometry unreadable" : null);
                    }
                    catch (Exception ex) { source = (null, source.Hash, $"asset read failed: {ex.GetType().Name}: {ex.Message}"); }
                }
                boundsCache[rel] = source;
            }
            ro.Issue = source.Issue;
            ro.AssetHash = source.Hash;
            if (!builtCache.TryGetValue(rel, out var built))
            {
                built = null;
                var abs = resolver.ResolveAssetPath(rel);
                if (abs is not null)
                {
                    try
                    {
                        built = builder.BuildFromFile(abs);
                        StripNifRootTransform(abs, built);
                        if (needOcclusion && source.Hash.Length > 0)
                        {
                            var renderedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(abs)));
                            if (renderedHash != source.Hash)
                                geometryInputIssues[rel] = "model asset changed between bounds and renderer reads";
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"BuildFromFile('{rel}')", ex);
                        geometryInputIssues[rel] = $"renderer geometry read failed: {ex.GetType().Name}: {ex.Message}";
                    }
                }
                builtCache[rel] = built;
            }
            if (geometryInputIssues.TryGetValue(rel, out var geometryInputIssue)) ro.Issue ??= geometryInputIssue;
            if (built is null || built.Count == 0)
            {
                missingMesh++; ro.Issue ??= "renderer produced no geometry";
                if (source.Model is { } unrendered)
                    foreach (var shape in unrendered.Shapes)
                        ro.Parts.Add(new ShapeOcclusion { Name = shape.Name, Block = shape.BlockIndex,
                            Stored = shape.StoredBound, AssetHash = source.Hash,
                            Issue = shape.BoundIssue ?? shape.PoseIssue ?? "source shape absent in renderer" });
                continue;
            }

            if (shapeDump is { Length: > 0 }
                && rel.Contains(shapeDump, StringComparison.OrdinalIgnoreCase)
                && shapeDumped.Add(rel))
            {
                Console.WriteLine($"   ---- shapes of {rel} ({built.Count} built) ----");
                foreach (var b in built)
                    Console.WriteLine(
                        $"      {(b.HasEffectShader ? "EFFECT " : "lighting")}  {b.ShapeName,-22}"
                      + $"  {b.Indices.Length / 3,6:N0} tri"
                      + $"  blend={(b.HasAlphaBlend ? $"{b.SrcBlendIndex}->{b.DstBlendIndex}" : "off")}"
                      + $"  matAlpha={b.MaterialAlpha:0.00}"
                      + (b.HasEffectShader
                          ? $"  base=({b.EffectBaseColor.X:0.00},{b.EffectBaseColor.Y:0.00},{b.EffectBaseColor.Z:0.00},a{b.EffectBaseColorAlpha:0.00})x{b.EffectBaseColorScale:0.00}"
                          : $"  emis=({b.EmissiveColor.X:0.00},{b.EmissiveColor.Y:0.00},{b.EmissiveColor.Z:0.00})x{b.EmissiveMultiple:0.00}")
                      + $"  zwrite={b.ZBufferWrite}"
                      + $"  tex0={(b.TexturePaths.TryGetValue(0, out var t0) && t0.Length > 0 ? t0 : "(none)")}");
            }

            // Skyrim-space placement — see SpatialOverlap.RefrRotation, which derives the whole
            // convention from the engine's own NiMatrix3::SetEulerAnglesXYZ. Two parts, found a
            // day apart because a yaw-only scene can only ever show the first: the angles are
            // NEGATED (2026-08-28, the Falkreath walkway crossing its own navmesh in an X), and
            // the axes compose Z FIRST (2026-08-30, the College dome arches flung off their ring
            // — invisible until a ref with all three angles non-zero came along).
            var rot = SpatialOverlap.RefrRotation(r.Rotation);
            var pos = new Vector3(r.Position.X, r.Position.Y, r.Position.Z);

            bool refHadFx = false, refHadFxStatic = false, refHadCovering = false;
            // The covering (if any) is a property of the ref's BASE, looked up once per ref.
            Covering? cover = coveringsByBase.Count > 0 && coveringsByBase.TryGetValue(r.BaseKey, out var cvv)
                            ? cvv : null;
            foreach (var b in built)
            {
                var matches = source.Model?.Shapes.Where(s => s.Name == b.ShapeName).ToArray();
                var original = matches is { Length: 1 } && built.Count(s => s.ShapeName == b.ShapeName) == 1 ? matches[0] : null;
                var part = new ShapeOcclusion { Name = b.ShapeName, AssetHash = source.Hash,
                    Block = original?.BlockIndex ?? -1, Stored = original?.StoredBound,
                    World = original?.StoredBound is { } sb ? SphereBound.Place(sb, r.Rotation, r.Scale, r.Position) : null,
                    Issue = original is null ? "renderer shape cannot be uniquely joined to a NIF block"
                        : original.BoundIssue ?? original.PoseIssue };
                ro.Parts.Add(part);
                if (r.Marker) part.Issue ??= "editor marker is not gameplay render geometry";
                if (original is not null && (original.VertexCount != b.Positions.Length || original.TriangleCount != b.Indices.Length / 3))
                    part.Issue ??= "renderer and source geometry counts disagree";
                if (b.HasEffectShader || b.HasAlphaBlend || b.HasAlphaTest || !b.ZBufferWrite || b.MaterialAlpha < .999f)
                    part.Issue ??= "transparent, alpha-tested, effect or non-depth-writing material unsupported";
                int n = b.Positions.Length;
                if (n == 0 || b.Indices.Length == 0) { part.Issue ??= "empty rendered geometry"; continue; }

                // UNSHADEABLE SHAPES — two kinds, both meaning "this renderer provably cannot
                // draw this shape's material, so drawing it is worse than omitting it". Per
                // SHAPE, not per model path: `Clutter\WoodFires\...Burning.nif` is one file
                // holding 5 solid shapes (logs, coals, ash) and 12 effect shapes, so no path
                // rule can drop the flames while keeping the logs. --fx puts both kinds back.
                //
                //  1. EFFECT SHADER. NifMeshBuilder reads material values off
                //     BSLightingShaderProperty ONLY, so a shape on a BSEffectShaderProperty
                //     (flames, smoke, light cones) arrives opaque, unflagged and fully lit and
                //     draws as a solid slab over the room. The material was never read; there is
                //     nothing to compensate for after the fact.
                //
                //  2. NORMAL MAP IN THE DIFFUSE SLOT. Slot 0 is sampled as diffuse. A shape
                //     whose slot 0 is a `_n.dds` therefore paints NORMAL-MAP COLOURS — the
                //     lilac/cyan/green of a tangent-space normal texture — under any lighting.
                //     This is what the iridescent slab over every lit hearth actually was
                //     (2026-08-28): `L2_Warp04:0`, Inferno's heat-shimmer plane, authored on a
                //     LIGHTING shader with `effects\VaporTileNormal_n.dds` in slots 0 and 1, so
                //     rule 1 does not see it. Bethesda's `_n` suffix is a hard convention — no
                //     legitimate diffuse carries it — and the count is reported separately so
                //     the rule's reach stays visible rather than assumed.
                bool normalAsDiffuse = b.TexturePaths.TryGetValue(0, out var slot0)
                                       && slot0.EndsWith("_n.dds", StringComparison.OrdinalIgnoreCase);
                // --fx-static rescues kind 1 ONLY. Kind 2 stays skipped under it: a normal map in
                // the diffuse slot is unshadeable however it is blended, and the warp plane is
                // exactly the slab this whole thread was about.
                bool drawStatic = fxStatic && b.HasEffectShader && !normalAsDiffuse;
                if (!keepFx && !drawStatic && (b.HasEffectShader || normalAsDiffuse))
                {
                    part.Issue ??= "unrenderable material omitted";
                    if (b.HasEffectShader) fxShapesSkipped++; else nmShapesSkipped++;
                    refHadFx = true;
                    fxByMesh[rel] = fxByMesh.TryGetValue(rel, out var already)
                        ? already
                        : built.Count(x => x.HasEffectShader
                                           || (x.TexturePaths.TryGetValue(0, out var s0)
                                               && s0.EndsWith("_n.dds", StringComparison.OrdinalIgnoreCase)));
                    continue;
                }
                PickShape? basePick = null;          // set when interactive; the covering links to it
                var wp = new Vector3[n];
                var wn = new Vector3[n];
                var wt = new Vector3[n];
                var wb = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    // Builder output is Y-up; un-swap into Z-up, place, swap back.
                    var mp = b.Positions[i]; var zp = new Vector3(mp.X, -mp.Z, mp.Y);
                    wp[i] = YUp(Vector3.Transform(zp * r.Scale, rot) + pos);
                    var mn = i < b.Normals.Length ? b.Normals[i] : Vector3.UnitY;
                    wn[i] = YUp(Vector3.TransformNormal(new Vector3(mn.X, -mn.Z, mn.Y), rot));
                    var mt = i < b.Tangents.Length ? b.Tangents[i] : Vector3.Zero;
                    wt[i] = YUp(Vector3.TransformNormal(new Vector3(mt.X, -mt.Z, mt.Y), rot));
                    var mb2 = i < b.Bitangents.Length ? b.Bitangents[i] : Vector3.Zero;
                    wb[i] = YUp(Vector3.TransformNormal(new Vector3(mb2.X, -mb2.Z, mb2.Y), rot));
                }

                // --cull-above: the dollhouse slice. A triangle is dropped only when ALL
                // THREE of its vertices sit above the cut — a wall spanning the cut keeps
                // standing (its lower verts anchor it) while a roof, vault or upper floor
                // wholly above it vanishes, which is what lets an interior top-down show
                // the storey the actors stand on instead of the shell over it. Viewer Y
                // IS Skyrim Z (YUp = (x, z, −y)), so the test reads wp[].Y directly.
                // Statics only: terrain, navmesh, NPC actors and the overlay are never
                // sliced — the subject of the picture must not be cullable by the knife
                // that exists to reveal it.
                var tri = b.Indices;
                if (cullAbove is { } cutZ)
                {
                    var keep = new List<int>(tri.Length);
                    for (int i = 0; i + 2 < tri.Length; i += 3)
                    {
                        if (wp[tri[i]].Y > cutZ && wp[tri[i + 1]].Y > cutZ && wp[tri[i + 2]].Y > cutZ)
                        { cullTrisDropped++; continue; }
                        keep.Add(tri[i]); keep.Add(tri[i + 1]); keep.Add(tri[i + 2]);
                    }
                    if (keep.Count != tri.Length) part.Issue ??= "geometry clipped; stored bound retained, audit unassessed";
                    if (keep.Count == 0) { cullShapesDropped++; continue; }   // shape wholly above the cut
                    if (keep.Count < tri.Length) tri = keep.ToArray();
                }

                var mesh = new GlMesh();
                mesh.Upload(Interleave(wp, wn, b.TextureCoordinates, wt, wb, b.VertexColors), tri);
                mesh.ShapeName = b.ShapeName;
                mesh.HasVertexColors = b.VertexColors is { Length: > 0 };
                mesh.UseAlphaTest = b.HasAlphaTest;
                mesh.AlphaThreshold = b.AlphaThreshold;
                mesh.HasAlphaBlend = b.HasAlphaBlend;
                mesh.IsDoubleSided = b.IsDoubleSided;
                mesh.MaterialAlpha = b.MaterialAlpha;
                mesh.DepthWrite = b.ZBufferWrite;
                mesh.IsDecal = b.IsDecal;
                mesh.SrcBlendIndex = b.SrcBlendIndex;
                // The DESTINATION factor was being dropped here (found 2026-08-28 while adding
                // --fx-static): only Src was copied, so GlMesh kept its default 7 = INV_SRC_ALPHA
                // and every ADDITIVE shape in the game — dst 0 = ONE — was composited as ordinary
                // alpha blending instead. Invisible on solid geometry, which is why it survived;
                // it is the whole ball game for a flame.
                mesh.DstBlendIndex = b.DstBlendIndex;
                mesh.IsModelSpace = b.IsModelSpaceNormals;

                if (r.Highlight && !refSurface)
                {
                    var haloColor = r.HighlightColor == default ? haloOrange : r.HighlightColor;
                    // The implicated ref keeps its OWN texture; the highlight is a thick orange
                    // OUTLINE — the same mesh inflated ~8u along its normals with flipped
                    // winding, drawn depth-tested + back-culled by the overlay pass, so it
                    // survives only where it pokes past the silhouette. (Was a flat orange
                    // repaint, which deleted every scale cue — Piranha, 2026-08-28.)
                    string? hd = b.TexturePaths.TryGetValue(0, out var hdp) ? hdp : null;
                    mesh.DiffuseTexture = hd is { Length: > 0 } ? textures.LoadTexture(hd) : textures.WhiteTexture;
                    if (mesh.DiffuseTexture != textures.WhiteTexture) texturedShapes++;

                    // Inflate from the shape's own centroid, NOT along stored normals — this
                    // door NIF's normals point inward, and a normal-inflated hull turns inside
                    // out. Then normalize each hull triangle's winding geometrically so the
                    // outward face is ALWAYS the culled one, whatever the source winding.
                    const float rim = 8f;
                    var centroid = Vector3.Zero;
                    for (int i = 0; i < n; i++) centroid += wp[i];
                    centroid /= n;
                    var sp = new Vector3[n];
                    for (int i = 0; i < n; i++)
                    {
                        var dir = wp[i] - centroid;
                        sp[i] = dir.LengthSquared() > 1e-6f ? wp[i] + Vector3.Normalize(dir) * rim : wp[i];
                    }
                    for (int i = 0; i + 2 < tri.Length; i += 3)
                    {
                        var p0 = sp[tri[i]]; var p1 = sp[tri[i + 1]]; var p2 = sp[tri[i + 2]];
                        var ng = Vector3.Cross(p1 - p0, p2 - p0);
                        var outward = Vector3.Dot(ng, (p0 + p1 + p2) / 3f - centroid) > 0f;
                        // Geometric normal pointing INWARD: seen from outside the hull, near
                        // faces are GL-back (culled) and only the far side survives — the rim
                        // where directly visible (solid Lequal pass), and the translucent ghost
                        // wherever something in the scene is closer (Greater pass reuses this
                        // same buffer). Far faces make the right ghost: a recessed door's jamb
                        // sits BETWEEN the hull's near and far planes, so a near-face ghost has
                        // a dead zone there (found by Piranha on door 02E14C), while the far
                        // face is behind every occluder by construction.
                        if (outward)
                        {
                            Overlay.Vertex(haloTris, p2, haloColor, Vector3.Zero);
                            Overlay.Vertex(haloTris, p1, haloColor, Vector3.Zero);
                            Overlay.Vertex(haloTris, p0, haloColor, Vector3.Zero);
                            Overlay.Vertex(haloNear, p0, haloColor, Vector3.Zero);
                            Overlay.Vertex(haloNear, p1, haloColor, Vector3.Zero);
                            Overlay.Vertex(haloNear, p2, haloColor, Vector3.Zero);
                        }
                        else
                        {
                            Overlay.Vertex(haloTris, p0, haloColor, Vector3.Zero);
                            Overlay.Vertex(haloTris, p1, haloColor, Vector3.Zero);
                            Overlay.Vertex(haloTris, p2, haloColor, Vector3.Zero);
                            Overlay.Vertex(haloNear, p2, haloColor, Vector3.Zero);
                            Overlay.Vertex(haloNear, p1, haloColor, Vector3.Zero);
                            Overlay.Vertex(haloNear, p0, haloColor, Vector3.Zero);
                        }
                    }
                }
                else if (r.Marker)
                {
                    // Markers are shown (only on --markers) as what they are: an editor overlay,
                    // not world geometry — their own texture at half strength over the scene.
                    string? md = b.TexturePaths.TryGetValue(0, out var mdp) ? mdp : null;
                    mesh.DiffuseTexture = md is { Length: > 0 } ? textures.LoadTexture(md) : orange;
                    mesh.HasAlphaBlend = true;
                    mesh.MaterialAlpha = 0.5f;
                    mesh.DepthWrite = false;
                }
                else
                {
                    string? diffuse = b.TexturePaths.TryGetValue(0, out var d) ? d : null;
                    string? normal = b.TexturePaths.TryGetValue(1, out var nrm) ? nrm : null;
                    mesh.DiffuseTexture = diffuse is { Length: > 0 } ? textures.LoadTexture(diffuse) : textures.WhiteTexture;
                    if (mesh.DiffuseTexture != textures.WhiteTexture) texturedShapes++;
                    if (normal is { Length: > 0 })
                    {
                        int h = textures.LoadTexture(normal);
                        if (h != textures.WhiteTexture) { mesh.NormalTexture = h; mesh.HasNormalMap = true; }
                    }
                }

                if (r.Highlight && refSurface)
                {
                    // An identity pass must preserve the scene depth and the normal material path.
                    // A solid overlay cannot reproduce alpha-card coverage, so these participants
                    // remain explicitly unqualified instead of becoming opaque diagnostic sheets.
                    if (b.HasAlphaBlend || b.HasAlphaTest || b.MaterialAlpha < 1f ||
                        !b.ZBufferWrite || b.HasEffectShader || r.Marker)
                        subjectSurfaceRefusals.Add(r.RefKey.ToString());
                    else
                    {
                        var surface = b.IsDoubleSided ? subjectSurfaceDoubleSided : subjectSurface;
                        var colour = r.HighlightColor == default ? haloOrange : r.HighlightColor;
                        foreach (var index in tri) Overlay.Vertex(surface, wp[index], colour, Vector3.Zero);
                    }
                }

                if (drawStatic)
                {
                    // THE STATIC-FRAME STAND-IN. Everything an effect shape needs is already in
                    // hand except its material: the source texture resolves (nifly maps slot 0 to
                    // BSEffectShaderProperty.sourceTexture), and the NiAlphaProperty — a separate
                    // block, so the builder reads it fine — already says additive with no depth
                    // write. What is missing is that BSEffectShaderProperty is never read, so the
                    // shape arrives emissive-zero and draws LIT: a dark quad, and a dark quad added
                    // to the frame adds nothing, which is why --fx showed no flames at all.
                    //
                    // So: force it unlit-ish by driving the existing emissive term (the shader
                    // computes albedo * (diffuse + emissive), so a white emissive x1 yields roughly
                    // albedo — the fire atlas's own colour — with the lighting no longer able to
                    // darken it toward nothing). This is a STAND-IN, not the real thing: the real
                    // thing reads baseColor / baseColorScale off the block and needs an unlit
                    // branch in basic.frag. It is deliberately cheap and reversible.
                    //
                    // "First frame" is free: the block's stored values ARE the base state, and the
                    // BSEffectShaderPropertyFloatControllers that would animate UV/alpha are simply
                    // never run. Nothing here has to fake a clock.
                    mesh.HasEmissive = true;
                    mesh.EmissiveColor = b.EffectBaseColor;
                    // The author's own baseColorScale (8-10 on this fire) is used as the intensity.
                    // It is an HDR number the engine tonemaps and viewprobe cannot, so it DOES
                    // clip — but it clips along the baseColor hue, which is what a fire core looks
                    // like anyway. The earlier attempt blew out to flat white only because the
                    // emissive was left WHITE, so every channel clipped equally; tinted, the same
                    // magnitude reads as flame. Measured against a hand-swept ladder (x1 too faint
                    // to see, x2 faint, x4 legible, x8 right) — and the authored value landing in
                    // that window is the reason to use it rather than a constant of my choosing.
                    // VP_FXSTATIC_EMIT scales it for taste.
                    mesh.EmissiveMultiple = b.EffectBaseColorScale * fxStaticEmit;
                    mesh.MaterialAlpha = b.EffectBaseColorAlpha;
                    mesh.DepthWrite = false;          // never let a frozen flame occlude the room
                    // Effect quads are authored double-sided (SLSF2_Double_Sided), but that flag
                    // lives on the shader block the builder never reads, so they arrive
                    // single-sided and roughly half of a flame rosette is back-face culled.
                    mesh.IsDoubleSided = true;
                    fxStaticShapes++;
                    refHadFxStatic = true;
                }

                renderer.AddMesh(mesh);
                meshes.Add(mesh);
                shapes++;
                ro.Meshes.Add(mesh);
                part.Meshes.Add(mesh);
                foreach (var vi in tri) ro.Grow(wp[vi]);

                // Interactive picking tests the SAME arrays the upload used (shared, not
                // copied): post-cull, post-filter — a hover names only what is drawn.
                if (interactive is not null)
                {
                    // Mesh comes along for the live height clip: the bounds just computed cover
                    // the DRAWN triangles only — post-cull, post --cull-above — so the band test
                    // costs nothing extra and cannot disagree with what is on screen. Viewer Y
                    // IS Skyrim z (YUp = (x, z, −y)), so Min.Y/Max.Y are the span it needs.
                    var ps = new PickShape { Kind = PickKind.Ref, Verts = wp, Tris = tri, Ref = r, Mesh = mesh };
                    ps.ComputeBounds();
                    interactive.PickShapes.Add(ps);
                    ro.Shapes.Add(ps);
                    part.Picks.Add(ps);
                    basePick = ps;                       // the covering below hangs itself off this
                }

                // ---- PROJECTED COVERING (MATO) ------------------------------------------------
                // A second shell over the shape just drawn, sharing its triangles: same verts,
                // per-vertex coverage in the ALPHA channel, UVs planar-projected along the
                // material's own projection vector. Emitted per SHAPE rather than per ref because
                // coverage is a function of each face's normal — one mesh can have a snowed roof
                // and bare undersides, and that difference IS the effect.
                if (cover is { } cv)
                {
                    var cu = new Vector2[n];
                    var cc = new Vector4[n];
                    var cp = new Vector3[n];
                    // Axes for the planar projection: -projection is "up" for this material, and
                    // any two perpendiculars do, since the material is noise with no authored
                    // orientation. Guard the degenerate cross by picking a seed axis that cannot
                    // be parallel to up.
                    var up = Unit(-cv.ProjectionVector);
                    if (up == Vector3.Zero) up = Vector3.UnitZ;
                    var seed = MathF.Abs(up.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
                    var t1 = Unit(Vector3.Cross(up, seed));
                    if (t1 == Vector3.Zero) t1 = Vector3.UnitX;
                    var t2 = Vector3.Cross(up, t1);
                    float cosMax = MathF.Cos(float.DegreesToRadians(Math.Clamp(cv.MaxAngleDeg, 0f, 180f)));
                    float uvScale = cv.UvScale > 1e-3f ? cv.UvScale : 256f;
                    float maxAlpha = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        // Back to Skyrim space: viewer Y-up is (x, z, -y) of Skyrim, so the
                        // inverse is (X, -Z, Y). Coverage and the projection are both Skyrim-space
                        // questions, and doing them in viewer space is how axes get silently swapped.
                        var sp = new Vector3(wp[i].X, -wp[i].Z, wp[i].Y);
                        var sn = Unit(new Vector3(wn[i].X, -wn[i].Z, wn[i].Y));
                        float d = Vector3.Dot(sn, up);
                        float a = d < cosMax ? 0f
                                : Math.Clamp((d - cv.FalloffBias) / MathF.Max(cv.FalloffScale, 1e-3f), 0f, 1f);
                        maxAlpha = MathF.Max(maxAlpha, a);
                        cc[i] = new Vector4(cv.Color.X, cv.Color.Y, cv.Color.Z, a);
                        cu[i] = new Vector2(Vector3.Dot(sp, t1) / uvScale, Vector3.Dot(sp, t2) / uvScale);
                        // Lift along the normal so the shell cannot z-fight its own base. IsDecal
                        // stops it writing depth but does NOT offset it, and coplanar geometry at
                        // this camera range stipples badly. 2 u is ~3 cm of Skyrim and invisible.
                        cp[i] = wp[i] + wn[i] * CoveringLift;
                    }
                    // Nothing catches the projection on this shape (a wall, an underside): emit
                    // nothing at all rather than a fully-transparent mesh. This is the common case
                    // and it is what keeps the covering pass from doubling the scene.
                    if (maxAlpha > 0.004f)
                    {
                        var covMesh = new GlMesh();
                        covMesh.Upload(Interleave(cp, wn, cu, wt, wb, cc), tri);
                        covMesh.ShapeName = $"covering:{cv.Label}";
                        covMesh.HasVertexColors = true;      // the shader's baseColor.a *= vertexColor.a
                        covMesh.HasAlphaBlend = true;
                        covMesh.SrcBlendIndex = 6;           // SRC_ALPHA
                        covMesh.DstBlendIndex = 7;           // INV_SRC_ALPHA — plain "over"
                        covMesh.DepthWrite = false;
                        covMesh.IsDecal = true;              // drawn after the sorted blend set, never writes depth
                        covMesh.IsDoubleSided = mesh.IsDoubleSided;
                        covMesh.DiffuseTexture = MatoTexture(cv.ModelPath);
                        renderer.AddMesh(covMesh);
                        meshes.Add(covMesh);
                        ro.Meshes.Add(covMesh);              // the snow is culled with its roof
                        part.Meshes.Add(covMesh);
                        // The live height clip walks PickShapes and a shell is deliberately not
                        // one of them; without this link, clipping a roof away left its snow
                        // hanging in the air.
                        if (basePick is not null) basePick.CoverMesh = covMesh;
                        coveringShapes++;
                        refHadCovering = true;
                        coveringByMato[cv.Label] = coveringByMato.TryGetValue(cv.Label, out var cn) ? cn + 1 : 1;
                    }
                }
            }
            if (source.Model is { } model)
                foreach (var unread in model.Shapes.Where(s => !ro.Parts.Any(p => p.Block == s.BlockIndex)))
                    ro.Parts.Add(new ShapeOcclusion { Name = unread.Name, Block = unread.BlockIndex,
                        Stored = unread.StoredBound, AssetHash = source.Hash, Issue = "source shape absent or ambiguous in renderer" });
            if (ro.Meshes.Count > 0 && r.BosSwapped) bosView.Swapped.Add(ro);
            if (ro.Meshes.Count > 0 && r.StateChange != 0) (r.StateArrives ? stateView.Arrived : stateView.Departed).Add(ro);
            if (refHadFx) fxRefCount++;
            if (refHadFxStatic) fxStaticRefCount++;
            if (refHadCovering) coveringRefs++;
        }

        // TERRAIN — winning LAND heightmap, drawn with each quadrant's winning base landscape
        // texture (LTEX -> TXST diffuse). Patches whose texture cannot be resolved fall back to
        // the off-white snow slope tint. Opaque, depth-written.
        int terrainTexturedPatches = 0, terrainFallbackPatches = 0;
        foreach (var (texPath, patchTris) in terrainPatches)
        {
            if (patchTris.Count == 0) continue;
            int texHandle = texPath.Length > 0 ? textures.LoadTexture(texPath) : textures.WhiteTexture;
            bool textured = texPath.Length > 0 && texHandle != textures.WhiteTexture;
            if (textured) terrainTexturedPatches++; else terrainFallbackPatches++;

            var tv = new List<Vector3>(); var ti = new List<int>();
            var tn = new List<Vector3>(); var tc = new List<Vector4>();
            var tuv = new List<Vector2>();
            foreach (var (a, b, c) in patchTris)
            {
                var nrm = Vector3.Cross(b - a, c - a);
                nrm = nrm.LengthSquared() > 1e-10f ? Vector3.Normalize(nrm) : Vector3.UnitZ;
                float flatness = MathF.Max(0f, nrm.Z);                    // Z-up before the swap
                // Textured ground: near-white vertex colour, slightly darker on steeps so relief
                // still reads. Where no base texture resolves — which in practice is the far
                // cells at the edge of the ring — the fallback is an OFF-WHITE slope tint that
                // reads as distant snow rather than as a green slab pretending to be foreground.
                var col = textured
                    ? Vector4.Lerp(new Vector4(0.75f, 0.74f, 0.72f, 1f), new Vector4(1f, 1f, 1f, 1f), flatness * flatness)
                    : Vector4.Lerp(new Vector4(0.46f, 0.48f, 0.52f, 1f), new Vector4(0.62f, 0.64f, 0.68f, 1f), flatness * flatness);
                int at = tv.Count;
                tv.Add(YUp(a)); tv.Add(YUp(b)); tv.Add(YUp(c));
                // Landscape textures tile; ~2 grid squares (256 units) per repeat reads close to
                // the game's default land tiling.
                tuv.Add(new Vector2(a.X / 256f, a.Y / 256f));
                tuv.Add(new Vector2(b.X / 256f, b.Y / 256f));
                tuv.Add(new Vector2(c.X / 256f, c.Y / 256f));
                var ny = YUp(nrm);
                tn.Add(ny); tn.Add(ny); tn.Add(ny);
                tc.Add(col); tc.Add(col); tc.Add(col);
                ti.Add(at); ti.Add(at + 1); ti.Add(at + 2);
            }
            var terra = new GlMesh();
            terra.Upload(Interleave(tv.ToArray(), tn.ToArray(), tuv.ToArray(), tn.ToArray(), tn.ToArray(), tc.ToArray()), ti.ToArray());
            terra.ShapeName = texPath.Length > 0 ? $"terrain:{texPath}" : "terrain";
            terra.DiffuseTexture = texHandle;
            terra.HasVertexColors = true;
            terra.IsDoubleSided = true;
            renderer.AddMesh(terra);
            meshes.Add(terra);
            if (interactive is not null)
            {
                var ps = new PickShape { Kind = PickKind.Terrain, Verts = tv.ToArray(), Tris = ti.ToArray(), TerrainTex = texPath };
                ps.ComputeBounds();
                interactive.PickShapes.Add(ps);
            }
        }
        // PAINTED LAYERS over the base. Roads, paths and every ground blend in Skyrim live here
        // and nowhere else, so a render without them is a render with no roads in it.
        //
        // They ride the library's DECAL channel: blended decals are drawn after the distance-
        // sorted transparent set, in the order they were added, which is the one thing coplanar
        // sheets actually need — a distance sort between two layers painted on the same triangle
        // is a coin flip, and layer order is the author's stated intent. Depth test on (a rock in
        // front still hides the road), depth write off (they are surface, not occluders).
        // Two env toggles for answering "is that wash really this texture, or is something wrong":
        // VP_LAYER_ONLY=<substr> keeps only layers whose diffuse path matches, and
        // VP_LAYER_SOLID=1 ignores the opacity map so the texture is drawn at full strength.
        // Together they isolate one painted layer and show it as itself.
        string? layerOnly = Environment.GetEnvironmentVariable("VP_LAYER_ONLY");
        bool layerSolid = Environment.GetEnvironmentVariable("VP_LAYER_SOLID") == "1";
        int layerMeshes = 0;
        foreach (var (texPath, layerTris) in terrainLayers)
        {
            if (layerTris.Count == 0) continue;
            if (layerOnly is { Length: > 0 } && !texPath.Contains(layerOnly, StringComparison.OrdinalIgnoreCase)) continue;
            int texHandle = textures.LoadTexture(texPath);
            if (texHandle == textures.WhiteTexture) continue;   // unresolvable: a white sheet is worse than none
            var lv = new List<Vector3>(); var li = new List<int>();
            var ln = new List<Vector3>(); var lc = new List<Vector4>();
            var luv = new List<Vector2>();
            foreach (var (a, b, c, oa, ob, oc) in layerTris)
            {
                var nrm = Vector3.Cross(b - a, c - a);
                nrm = nrm.LengthSquared() > 1e-10f ? Vector3.Normalize(nrm) : Vector3.UnitZ;
                float flatness = MathF.Max(0f, nrm.Z);
                float shade = 0.75f + 0.25f * flatness * flatness;   // same relief cue as the base
                int at = lv.Count;
                lv.Add(YUp(a)); lv.Add(YUp(b)); lv.Add(YUp(c));
                luv.Add(new Vector2(a.X / 256f, a.Y / 256f));
                luv.Add(new Vector2(b.X / 256f, b.Y / 256f));
                luv.Add(new Vector2(c.X / 256f, c.Y / 256f));
                var ny = YUp(nrm);
                ln.Add(ny); ln.Add(ny); ln.Add(ny);
                lc.Add(new Vector4(shade, shade, shade, layerSolid ? 1f : oa));
                lc.Add(new Vector4(shade, shade, shade, layerSolid ? 1f : ob));
                lc.Add(new Vector4(shade, shade, shade, layerSolid ? 1f : oc));
                li.Add(at); li.Add(at + 1); li.Add(at + 2);
            }
            var sheet = new GlMesh();
            sheet.Upload(Interleave(lv.ToArray(), ln.ToArray(), luv.ToArray(), ln.ToArray(), ln.ToArray(), lc.ToArray()), li.ToArray());
            sheet.ShapeName = $"terrain-layer:{texPath}";
            sheet.DiffuseTexture = texHandle;
            sheet.HasVertexColors = true;      // the opacity map rides in vertex alpha
            sheet.HasAlphaBlend = true;
            sheet.IsDecal = true;              // draw after the sorted set, in insertion order
            sheet.DepthWrite = false;
            sheet.IsDoubleSided = true;
            renderer.AddMesh(sheet);
            meshes.Add(sheet);
            layerMeshes++;
        }
        if (layerMeshes > 0) Console.WriteLine($"   terrain paint: {layerMeshes} painted layer sheet(s) drawn over the base");

        if (terrainTexturedPatches + terrainFallbackPatches > 0)
            Console.WriteLine($"   terrain paint: {terrainTexturedPatches} textured patch(es), {terrainFallbackPatches} untextured (snow fallback)");

        // ---- WATER --------------------------------------------------------------------------
        // One grid per loaded cell that has water, at that cell's own surface height. A GRID and
        // not a quad for one reason that earns its cost: the shallow-to-deep colour is a function
        // of the real distance down to the LAND underneath, so a shoreline needs vertices along it
        // to shade against. A single quad would paint the whole sea one flat colour and lose the
        // one cue that makes water read as water from above.
        //
        // Depth-tested, never depth-writing: terrain and statics standing above the surface hide
        // it for free, exactly as they should, so there is no need to clip the grid to the coast.
        foreach (var wpl in waterPlanes)
        {
            const int WaterGrid = 48;                 // 4096/48 ~ 85 u per quad — fine enough for a shoreline
            // Depth over which shallow fades to deep AND over which the surface closes up to
            // opaque. ~2.7 m of Skyrim: wading depth. Was 700 (≈10 m), which kept almost every
            // real body of water permanently in the "shallow" half of both ramps and is why the
            // sea read as tinted glass.
            const float ShallowBand = 192f;
            // The alpha at zero depth. 1 = opaque everywhere, matching the engine's opaque draw.
            float shoreAlpha = float.TryParse(Environment.GetEnvironmentVariable("VP_WATER_SHALLOW_ALPHA"),
                                              out var sa) ? Math.Clamp(sa, 0f, 1f) : 1f;
            int texHandle = wpl.NoiseTexture is { Length: > 0 } wtx ? textures.LoadTexture(wtx) : 0;
            bool hasNoise = texHandle != 0 && texHandle != textures.WhiteTexture;

            int side = WaterGrid + 1;
            int submerged = 0;                        // grid points whose LAND sits BELOW the surface
            var pv = new Vector3[side * side]; var nv = new Vector3[side * side];
            var uvv = new Vector2[side * side]; var cvv2 = new Vector4[side * side];
            var tgt = new Vector3[side * side]; var btg = new Vector3[side * side];
            var idxs = new List<int>(WaterGrid * WaterGrid * 6);
            float uvScale = wpl.NoiseUvScale > 1e-3f ? wpl.NoiseUvScale : 1024f;
            for (int gy = 0; gy < side; gy++)
                for (int gx = 0; gx < side; gx++)
                {
                    int i = gy * side + gx;
                    float x = wpl.X0 + (wpl.X1 - wpl.X0) * gx / WaterGrid;
                    float y = wpl.Y0 + (wpl.Y1 - wpl.Y0) * gy / WaterGrid;
                    // Depth to the ground below. No LAND here (off the heightmap) reads as deep
                    // ocean rather than as shallow — the conservative direction for a coastline.
                    float? groundZ = terrainHeightAt?.Invoke(x, y);
                    float depth = groundZ is { } gz ? wpl.Z - gz : ShallowBand;
                    if (depth > 0f) submerged++;
                    float t = Math.Clamp(depth / ShallowBand, 0f, 1f);
                    var body = Vector3.Lerp(wpl.ShallowColor, wpl.DeepColor, t);

                    // SKY REFLECTION, Fresnel-weighted by the record's own WaterFresnel as F0
                    // (Schlick). This replaced a flat `lerp(body, sky, WaterReflectivity)` that
                    // mixed 40% pale sky into every vertex regardless of angle and turned the
                    // Riverwood ford into a grey-green film over sand — the water was drawing
                    // correctly and simply did not READ as water. Top-down, Schlick with an F0 of
                    // 0.07 returns almost nothing, so the surface shows its own dark teal, which
                    // is what water looks like from above; at grazing angles it goes bright.
                    // Camera-dependent and therefore baked at the ENTRY camera, like the rest of
                    // this renderer's view-dependent overlay work — exact for a static render,
                    // frozen in the fly-through.
                    var toEye = Unit(new Vector3(cam.Eye.X - x, cam.Eye.Y - y, cam.Eye.Z - wpl.Z));
                    float cosI = MathF.Max(toEye.Z, 0f);              // surface normal is +Z
                    float f0 = Math.Clamp(wpl.Fresnel, 0f, 1f);
                    float fres = f0 + (1f - f0) * MathF.Pow(1f - cosI, 5f);
                    var reflected = skyColor * wpl.ReflectionColor;   // sky, tinted by WATR's own reflection colour
                    var rgb = Vector3.Lerp(body, reflected, Math.Clamp(fres * wpl.Reflectivity * 4f, 0f, 1f));

                    pv[i] = YUp(new Vector3(x, y, wpl.Z));
                    nv[i] = YUp(Vector3.UnitZ);
                    tgt[i] = YUp(Vector3.UnitX);
                    btg[i] = YUp(Vector3.UnitY);
                    uvv[i] = new Vector2(x / uvScale, y / uvScale);
                    // ALPHA. Grounded in the game's own water shader (Community Shaders'
                    // `package/Shaders/Water.hlsl`), which settles a question guesswork got wrong
                    // twice here: **Skyrim does not alpha-blend its water at all.** The pixel
                    // shader writes `psout.Lighting = float4(finalColor, isSpecular)` — the alpha
                    // channel carries a specular FLAG, not opacity — so the surface is an OPAQUE
                    // draw, and everything that looks like transparency is done inside the shader
                    // as `lerp(refractionColor, finalColorPreFog, refractionMul)` against a
                    // refraction sample. Framebuffer blending is the wrong instrument; it is only
                    // used here because this renderer has no refraction buffer to lerp against,
                    // and a blend IS that same lerp with the framebuffer standing in for it.
                    //
                    // The shape of the ramp is the shader's too. `VarAmounts` is documented in its
                    // own cbuffer as "alpha in z", and that value is a FLOOR rather than a level:
                    //     distanceMul = saturate(lerp(VarAmounts.z, 1, -(distanceFactor - 1)))
                    // i.e. it starts at the record's alpha and rises to 1 — fully opaque, no
                    // refraction — as the driving factor closes. So WATR.Opacity is the MOST
                    // transparent the water ever gets, not how transparent it is. Using it as a
                    // flat 50% everywhere (the previous version) made an ocean out of tinted air.
                    //
                    // Honest divergence, stated rather than buried: in the engine that driving
                    // factor is camera distance and fog, not depth to the bottom. Depth is used
                    // here instead because this is a renderer for floor plans and site pictures,
                    // where "you can see the riverbed through a ford but not through a fjord" is
                    // the readable, camera-independent behaviour, and because a camera-distance
                    // ramp would bake into vertex colours and freeze at the entry camera.
                    // So: OPAQUE by default, which is what the engine draws. The shallow/deep
                    // information is not lost by that — it rides the COLOUR ramp above, which is
                    // the shader's own `lerp(ShallowColor, DeepColor, distanceMul)`. What is lost
                    // is seeing the bed through a ford, and that is a refraction effect this
                    // renderer cannot do honestly. VP_WATER_SHALLOW_ALPHA=<0..1> puts a
                    // see-through shore margin back for anyone who wants it for a floor plan.
                    cvv2[i] = new Vector4(rgb.X, rgb.Y, rgb.Z, shoreAlpha + (1f - shoreAlpha) * t);
                }
            for (int gy = 0; gy < WaterGrid; gy++)
                for (int gx = 0; gx < WaterGrid; gx++)
                {
                    // Wind so the geometric normal comes out +Y (up) in viewer space, agreeing
                    // with the vertex normals. Viewer Z is -y, so advancing gy walks -Z and the
                    // "obvious" order faces the surface at the sea floor instead of the sky.
                    int a = gy * side + gx, b2 = a + 1, c2 = a + side, d2 = c2 + 1;
                    idxs.Add(a); idxs.Add(b2); idxs.Add(c2);
                    idxs.Add(b2); idxs.Add(d2); idxs.Add(c2);
                }
            var water = new GlMesh();
            water.Upload(Interleave(pv, nv, uvv, tgt, btg, cvv2), idxs.ToArray());
            water.ShapeName = $"water:{wpl.Label}";
            water.DiffuseTexture = textures.WhiteTexture;   // WATR has NO diffuse field; colour is the record's
            if (hasNoise)
            {
                // The noise layer is a WAVE NORMAL MAP, bound as one. Binding it as a diffuse
                // would paint the sea normal-map lilac — the exact defect class this renderer
                // already carries a filter for.
                water.NormalTexture = texHandle;
                water.HasNormalMap = true;
            }
            water.HasVertexColors = true;
            // OPAQUE water joins the opaque pass and writes depth; only a see-through shore margin
            // needs blending. This is not just tidiness — it fixes a layering bug that the opaque
            // default exposed. Terrain paint layers ride the DECAL channel, and decals are drawn
            // AFTER the distance-sorted blend set, so while water was blended the roads and
            // ground blends composited straight over the top of it: quadrant-shaped rectangles of
            // differing tint sitting on the river. As opaque geometry the water is simply in the
            // depth buffer before they draw, and they are correctly hidden beneath it.
            bool opaqueWater = shoreAlpha >= 0.999f;
            water.HasAlphaBlend = !opaqueWater;
            water.SrcBlendIndex = 6; water.DstBlendIndex = 7;
            water.DepthWrite = opaqueWater;
            water.IsDoubleSided = true;                     // readable from under the surface too
            water.MaterialGlossiness = 80f;
            water.MaterialSpecularStrength = 0.5f;
            renderer.AddMesh(water);
            meshes.Add(water);
            waterMeshes++; waterTris += idxs.Count / 3;
            waterSubmerged += submerged; waterSamples += side * side;
        }
        if (waterMeshes > 0)
            Console.WriteLine($"   water: {waterMeshes} cell surface(s), {waterTris:N0} triangles — "
                            + $"{waterPlanes[0].Label} at z {waterPlanes[0].Z:0}"
                            + (waterPlanes.Any(w => w.NoiseTexture is { Length: > 0 })
                                ? ", noise layer bound as a wave normal map" : ", no noise texture resolved")
                            + " (no reflection/refraction/flow — see README)");
        // HOW MUCH of the water footprint is actually below the surface. The number that tells a
        // wrong HEIGHT from an honest absence: a sea whose floor is mostly above the surface is a
        // height bug, while a low percentage over a mountain cell is just a mountain. Without it,
        // "9 cell surface(s) drawn" reads as success whether or not one pixel of water is visible.
        if (waterSamples > 0)
            Console.WriteLine($"      {100.0 * waterSubmerged / waterSamples:0.0}% of the water footprint has LAND "
                            + $"BELOW the surface (the rest is dry ground, and correctly hides it)");

        // ── THE NAVMESH OVERLAY — the probe's whole point ──────────────────────────────
        // Looks: filled = the translucent sheet; wire = every triangle outlined; both.
        //
        // The WIRE is drawn by viewprobe ITSELF, in a pass of its own after the library's,
        // because navmesh the camera cannot see still has to be reportable. Every edge is
        // walked in short steps and each step classified:
        //
        //   visible       drawn SOLID and still depth-tested — the picture as it always was;
        //   subterranean  the step sits below the winning LAND surface at its own X,Y, i.e.
        //                 the navmesh is buried — redrawn as DASHES with the depth test off;
        //   hidden        above ground but behind something the camera can see (a rock, a
        //                 wall, a cart), per the world-only depth prepass — also DASHES.
        //
        // Dashes say "this is here, you just cannot see it" without lying about visibility.
        // Turning a dash category off puts those steps back in the SOLID layer, where the
        // depth buffer hides them again — i.e. off == the pre-dash picture, exactly.
        //
        // COLOUR. Red is RESERVED for the finding. The healthy colour defaults to "auto":
        // the world is rendered once WITHOUT the overlay, the pixels underneath the navmesh
        // are sampled, and the palette colour farthest from both that background and the
        // reserved red wins. --nav-color <name> forces one.
        var lift = new Vector3(0, 0, 2f);                           // Z-up lift before the swap

        GlMesh BuildFill(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> tris, Vector4 color, string name)
        {
            var nv = new List<Vector3>(); var ni = new List<int>();
            foreach (var (a, b, c) in tris)
            {
                int at = nv.Count;
                nv.Add(YUp(a + lift)); nv.Add(YUp(b + lift)); nv.Add(YUp(c + lift));
                ni.Add(at); ni.Add(at + 1); ni.Add(at + 2);
            }
            var flat = new Vector3[nv.Count]; var uv = new Vector2[nv.Count];
            var col = new Vector4[nv.Count];
            for (int i = 0; i < nv.Count; i++) { flat[i] = Vector3.UnitY; col[i] = color; }
            var mm = new GlMesh();
            mm.Upload(Interleave(nv.ToArray(), flat, uv, flat, flat, col), ni.ToArray());
            mm.ShapeName = name;
            mm.DiffuseTexture = textures.WhiteTexture;
            mm.HasVertexColors = true;
            mm.HasAlphaBlend = true;
            mm.MaterialAlpha = color.W;
            mm.IsDoubleSided = true;
            mm.DepthWrite = false;
            return mm;
        }

        var red = new Vector4(1f, 0.12f, 0.05f, 1f);

        // ---- placed NPC actors -----------------------------------------------------------
        // Their meshes were built and WORLD-BAKED by NpcActorBuilder on this same GL context
        // and are BORROWED into this panel's renderer: they must draw in every pass (the
        // occlusion prepass included — a navmesh behind an actor is genuinely hidden), but
        // this renderer's Dispose()/ClearMeshes() DISPOSES what it holds, so every borrowed
        // mesh is RemoveMesh'd again before this method returns. The owning VMs outlive all
        // panels and are disposed by Program.cs after the last one.
        var borrowedNpcMeshes = new List<GlMesh>();
        foreach (var actor in npcActors)
            foreach (var am in actor.Meshes)
            {
                renderer.AddMesh(am);
                borrowedNpcMeshes.Add(am);
            }

        // First-person camera: eye/look given in Skyrim coordinates, expressed on the OrbitCamera.
        // Near plane 15 u = the game's `fNearDistance:Display` (read off the running engine 2026-09-05: the
        // compiled-in default, no profile ini sets it; first person uses `fNear1stPersonDistance` 5). It was 4 u
        // before, which drew geometry the game clips in a close-up. That the free camera uses the third-person
        // value is assumed, not measured.
        const float NearPlane = 15f, FarPlane = 131072f;
        var eyeY = YUp(cam.Eye); var lookY = YUp(cam.Look);
        var camOff = eyeY - lookY;
        float camDist = MathF.Max(camOff.Length(), 1f);
        var camera = new OrbitCamera
        {
            Target = new GlVec3(lookY.X, lookY.Y, lookY.Z),
            Distance = camDist,
            // Clamped off vertical. OrbitCamera builds its view with Matrix4.LookAt(..., UnitY),
            // which DEGENERATES when the view direction is parallel to up: an exactly-straight-down
            // camera (--eye X,Y,Z --look X,Y,Z-N) produced a NaN view matrix and a frame of pure sky,
            // with nothing in the console to say so. The flat plan path already shoots 89.5f for the
            // same reason; this is that guard on the textured path.
            Elevation = Math.Clamp(float.RadiansToDegrees(MathF.Asin(camOff.Y / camDist)), -89.5f, 89.5f),
            Azimuth = float.RadiansToDegrees(MathF.Atan2(camOff.X, camOff.Z)),
            FieldOfView = cam.FovDeg,
            NearPlane = NearPlane,
            FarPlane = FarPlane,
            MaxDistance = 1_000_000f,
        };

        // Offscreen target + readback (same pattern as the flat path). With MSAA the scene is
        // drawn into a multisampled PAIR and resolved into this one: --size buys detail, but the
        // overlay's diagonal ribbons are edges, and edges are what multisampling fixes.
        int fbo = GL.GenFramebuffer(), colorTex = GL.GenTexture(), depthRb = GL.GenRenderbuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.BindTexture(TextureTarget.Texture2D, colorTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, sizeW, sizeH, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colorTex, 0);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRb);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, sizeW, sizeH);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depthRb);

        int samples = 0, msFbo = 0, msColorRb = 0, msDepthRb = 0;
        if (msaa >= 2)
        {
            GL.GetInteger(GetPName.MaxSamples, out int maxSamples);
            samples = Math.Min(msaa, Math.Max(1, maxSamples));
        }
        if (samples >= 2)
        {
            msFbo = GL.GenFramebuffer(); msColorRb = GL.GenRenderbuffer(); msDepthRb = GL.GenRenderbuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, msFbo);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msColorRb);
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, samples, RenderbufferStorage.Rgba8, sizeW, sizeH);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, msColorRb);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msDepthRb);
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, samples, RenderbufferStorage.DepthComponent24, sizeW, sizeH);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, msDepthRb);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            {
                // Never silently: a render that quietly lost its anti-aliasing looks like a
                // renderer that never had any.
                Console.WriteLine($"   msaa: {samples}x framebuffer incomplete -- rendering without multisampling");
                GL.DeleteFramebuffer(msFbo); GL.DeleteRenderbuffer(msColorRb); GL.DeleteRenderbuffer(msDepthRb);
                msFbo = 0; samples = 0;
            }
        }
        int drawFbo = msFbo != 0 ? msFbo : fbo;

        // viewprobe's own wire pass, installed once built. It takes the view/projection to draw
        // with: the static path hands it the entry camera's fixed matrices; the interactive loop
        // hands it each frame's.
        Action<OpenTK.Mathematics.Matrix4, OpenTK.Mathematics.Matrix4>? wireOverlay = null;

        byte[] Draw(float[]? depthOut = null, bool singleSample = false)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, singleSample ? fbo : drawFbo);
            GL.Viewport(0, 0, sizeW, sizeH);
            GL.ClearColor(skyColor.X, skyColor.Y, skyColor.Z, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.Render(camera, sizeW, sizeH);   // clears again, with renderer.ClearColor = sky
            wireOverlay?.Invoke(camera.GetViewMatrix(), camera.GetProjectionMatrix(canvasAspect));
            if (msFbo != 0 && !singleSample)
            {
                // Resolve colour AND depth: the occlusion test reads the depth buffer back, so it
                // has to come from the same resolved frame the pixels did.
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, msFbo);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
                GL.BlitFramebuffer(0, 0, sizeW, sizeH, 0, 0, sizeW, sizeH,
                                   ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit,
                                   BlitFramebufferFilter.Nearest);
            }
            GL.Finish();
            var px = new byte[sizeW * sizeH * 4];
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.ReadPixels(0, 0, sizeW, sizeH, PixelFormat.Rgba, PixelType.UnsignedByte, px);
            if (depthOut is not null)
                GL.ReadPixels(0, 0, sizeW, sizeH, PixelFormat.DepthComponent, PixelType.Float, depthOut);
            return px;
        }

        bool wantNav = (navTris.Count > 0 || redTris.Count > 0) && navMode != "off";
        // --interactive builds BOTH the sheet and the wire whatever --nav asked for, so the N
        // key switches display modes by flipping visibility instead of rebuilding the scene.
        // It costs the wire's classification pass on a run that only asked for a sheet; that is
        // the price of an instant toggle, and it is paid once at startup.
        bool bothForFly = interactive is not null && wantNav;
        bool wireMode = bothForFly || navMode is "wire" or "both";
        bool fillMode = bothForFly || navMode is "filled" or "both";
        if (interactive is not null) interactive.NavMode = wantNav ? navMode : "off";
        var palette = new (string Name, Vector4 Color)[]
        {
            ("green",   new Vector4(0.05f, 0.85f, 0.30f, 1f)),
            ("cyan",    new Vector4(0.05f, 0.85f, 0.95f, 1f)),
            ("magenta", new Vector4(0.90f, 0.20f, 0.95f, 1f)),
            ("yellow",  new Vector4(1f, 0.88f, 0.10f, 1f)),
            ("blue",    new Vector4(0.25f, 0.45f, 1f, 1f)),
        };

        // The camera's OWN matrices, not a hand-rolled replica: everything that has to agree
        // with what was rasterised (the colour sample, the depth compare) goes through these.
        var viewMat = camera.GetViewMatrix();
        var projMat = camera.GetProjectionMatrix(canvasAspect);
        var viewProj = viewMat * projMat;
        var viewFwd = Vector3.Normalize(lookY - eyeY);

        // Screen pixel for a Y-up world point, or null when it is off-frame. GL's buffer
        // origin is bottom-left, which matches NDC directly — every sample below happens
        // BEFORE the vertical flip, so no Y inversion belongs here.
        (int X, int Y)? ScreenOf(Vector3 pYUp)
        {
            var clip = GlVec4.TransformRow(new GlVec4(pYUp.X, pYUp.Y, pYUp.Z, 1f), viewProj);
            if (clip.W <= 0.01f) return null;
            float nx = clip.X / clip.W, ny = clip.Y / clip.W, nz = clip.Z / clip.W;
            if (nx < -1f || nx > 1f || ny < -1f || ny > 1f || nz < -1f || nz > 1f) return null;
            return (Math.Clamp((int)((nx * 0.5f + 0.5f) * sizeW), 0, sizeW - 1),
                    Math.Clamp((int)((ny * 0.5f + 0.5f) * sizeH), 0, sizeH - 1));
        }

        // ── THE WORLD-COORDINATE GRID ────────────────────────────────────────────────
        // Ruled across ONE PLANE — the storey the shot is about — because that is the only thing
        // a grid can honestly be: two points at different heights under the same pixel have
        // different X,Y, and a grid that pretended otherwise would be a ruler with no scale.
        //
        // It is built here, beside the camera matrices, because both halves need them: the LINES
        // are world geometry (they ride the overlay pass like everything else), and the LABELS are
        // screen positions found by projecting each line and clipping it to the frame. Nothing is
        // measured off the finished picture.
        var gridRibbon = new List<float>();
        var gridLabels = new List<(string Text, int X, int Y)>();
        float gridSpacing = 0f;
        var gridColour3 = grid is null ? Vector3.Zero : new Vector3(grid.Color.X, grid.Color.Y, grid.Color.Z);
        if (grid is not null)
        {
            var invVpG = viewProj.Inverted();
            // Where the ray through an NDC point meets the grid's plane, in Skyrim coordinates.
            // Null when the ray runs parallel to the plane or meets it behind the camera — i.e.
            // when the horizon is in shot, which is exactly the case a ground grid cannot serve.
            Vector3? PlaneHit(float nx, float ny)
            {
                var c0 = GlVec4.TransformRow(new GlVec4(nx, ny, -1f, 1f), invVpG);
                var c1 = GlVec4.TransformRow(new GlVec4(nx, ny, 1f, 1f), invVpG);
                if (MathF.Abs(c0.W) < 1e-9f || MathF.Abs(c1.W) < 1e-9f) return null;
                var a = new Vector3(c0.X / c0.W, c0.Y / c0.W, c0.Z / c0.W);
                var b = new Vector3(c1.X / c1.W, c1.Y / c1.W, c1.Z / c1.W);
                var pa = new Vector3(a.X, -a.Z, a.Y);            // Y-up back to Skyrim's Z-up
                var pb = new Vector3(b.X, -b.Z, b.Y);
                float dz = pb.Z - pa.Z;
                if (MathF.Abs(dz) < 1e-6f) return null;
                float t = (grid.Z - pa.Z) / dz;
                if (t < 0f || t > 1f) return null;
                return pa + (pb - pa) * t;
            }
            // Pitch measured off the camera's OWN forward vector, not off the arguments that
            // composed it, so a pose reached by --yaw/--pitch and one reached by --look are judged
            // the same way. 90 deg is straight down.
            float pitchDown = float.RadiansToDegrees(MathF.Asin(Math.Clamp(-viewFwd.Y, -1f, 1f)));
            const float SteepEnough = 60f;
            var hits = new[] { PlaneHit(-1f, -1f), PlaneHit(1f, -1f), PlaneHit(-1f, 1f), PlaneHit(1f, 1f) };
            if (pitchDown < SteepEnough)
                // Declined, not drawn badly. At eye level the lines converge on the horizon, the
                // near cells swallow the frame and the far ones stack into a smear — a picture
                // that looks like a scale and is not one. The console says so; the image does not
                // pretend a grid was asked for.
                Console.WriteLine($"   grid: DECLINED — the camera looks down {pitchDown:0.#}°, and a ground grid only reads "
                                + $"on a steep top-down ({SteepEnough:0}° or more). Nothing was drawn.");
            else if (hits.Any(h => h is null))
                Console.WriteLine($"   grid: DECLINED — the plane at z {grid.Z:0} leaves the frame (the horizon is in shot), "
                                + "so part of the picture has no grid position at all. Nothing was drawn.");
            else
            {
                var pts4 = hits.Select(h => h!.Value).ToArray();
                float minX = pts4.Min(p => p.X), maxX = pts4.Max(p => p.X);
                float minY = pts4.Min(p => p.Y), maxY = pts4.Max(p => p.Y);
                float span = MathF.Max(maxX - minX, maxY - minY);
                // AUTO: a 1-2-5 round number, chosen so about nine lines cross the frame. Round
                // because the labels are read by a person — 250 and 500 are numbers anyone can
                // interpolate between by eye, 273 is not — and nine because past a dozen the
                // labels start colliding and the grid becomes hatching.
                if (grid.Spacing is { } fixedN) gridSpacing = fixedN;
                else
                {
                    const float TargetLines = 9f;
                    float want = MathF.Max(span / TargetLines, 1e-3f);
                    float bestErr = float.MaxValue;
                    // 1-2-2.5-5 rather than plain 1-2-5. Without the 2.5 the ladder has a gap
                    // between 200 and 500 wide enough that a 2,500 u frame — an ordinary interior
                    // top-down — lands on 13 lines or 5 and never near the 9 that was asked for.
                    for (int e = -2; e <= 6; e++)
                        foreach (float m in new[] { 1f, 2f, 2.5f, 5f })
                        {
                            float v = m * MathF.Pow(10f, e);
                            float err = MathF.Abs(MathF.Log(v) - MathF.Log(want));
                            if (err < bestErr) { bestErr = err; gridSpacing = v; }
                        }
                }
                int nx0 = (int)MathF.Ceiling(minX / gridSpacing), nx1 = (int)MathF.Floor(maxX / gridSpacing);
                int ny0 = (int)MathF.Ceiling(minY / gridSpacing), ny1 = (int)MathF.Floor(maxY / gridSpacing);
                long lineCount = (long)Math.Max(0, nx1 - nx0 + 1) + Math.Max(0, ny1 - ny0 + 1);
                const int MaxLines = 400;
                if (lineCount > MaxLines)
                {
                    // Refusing to draw 4,000 lines is not timidity: past this the grid is a fill,
                    // it hides the render underneath it, and the labels are unreadable anyway.
                    Console.WriteLine($"   grid: DECLINED — spacing {gridSpacing:0.##} u would rule {lineCount:N0} lines across "
                                    + $"a {span:0} u frame, which is a hatch, not a scale. Widen the spacing or pass --grid auto.");
                    gridSpacing = 0f;
                }
                else if (lineCount == 0)
                {
                    Console.WriteLine($"   grid: DECLINED — spacing {gridSpacing:0.##} u puts no line inside a {span:0} u frame.");
                    gridSpacing = 0f;
                }
                else
                {
                    // NDC for a Skyrim point, un-clamped and with the off-frame case kept — the
                    // label hunt below needs to know WHERE a line leaves the frame, which the
                    // clamping ScreenOf above deliberately throws away.
                    (float X, float Y)? Ndc(Vector3 sky)
                    {
                        var y = YUp(sky);
                        var c = GlVec4.TransformRow(new GlVec4(y.X, y.Y, y.Z, 1f), viewProj);
                        return c.W <= 0.01f ? null : (c.X / c.W, c.Y / c.W);
                    }
                    // A grid line is thinner than the route's and thinner than the nav wire's, on
                    // purpose: it is the paper the picture is drawn on, not a thing in the picture.
                    static float GridHalfWidth(float d) => Math.Clamp(d * 0.00085f, 0.35f, 14f);
                    // Subdivided rather than drawn as one quad so the ribbon's width tracks
                    // distance along its own length, the way every other overlay ribbon does.
                    const int Steps = 24;

                    void RuleLine(Vector3 a, Vector3 b, string label)
                    {
                        var poly = new Vector3[Steps + 1];
                        for (int i = 0; i <= Steps; i++) poly[i] = Vector3.Lerp(a, b, (float)i / Steps);
                        Overlay.SolidPolyline(gridRibbon, poly, gridColour3 / 1.5f, GridHalfWidth, cam.Eye);

                        // The label goes where the line MEETS THE FRAME EDGE, found by clipping the
                        // projected segment to the NDC square (Liang-Barsky). Reading the crossing
                        // off the projection is the whole point: it is right for a rotated camera,
                        // where "constant X" does not run down the screen.
                        if (Ndc(a) is not { } p || Ndc(b) is not { } q) return;
                        float dx = q.X - p.X, dy = q.Y - p.Y;
                        float t0 = 0f, t1 = 1f;
                        bool Clip(float pp, float qq)
                        {
                            if (MathF.Abs(pp) < 1e-9f) return qq >= 0f;
                            float r = qq / pp;
                            if (pp < 0f) { if (r > t1) return false; if (r > t0) t0 = r; }
                            else { if (r < t0) return false; if (r < t1) t1 = r; }
                            return true;
                        }
                        if (!Clip(-dx, p.X + 1f) || !Clip(dx, 1f - p.X)
                         || !Clip(-dy, p.Y + 1f) || !Clip(dy, 1f - p.Y)) return;
                        (int X, int Y) Pix(float t)
                        {
                            float nx = p.X + dx * t, ny = p.Y + dy * t;
                            int ix = (int)MathF.Round((nx * 0.5f + 0.5f) * sizeW);
                            int iy = (int)MathF.Round((ny * 0.5f + 0.5f) * sizeH);
                            return (Math.Clamp(ix, 0, sizeW - 1), Math.Clamp(sizeH - 1 - iy, 0, sizeH - 1));  // GL bottom-left -> image top-left
                        }
                        var e0 = Pix(t0); var e1 = Pix(t1);
                        // Vertical on screen -> label along the TOP edge; horizontal -> along the
                        // LEFT edge. Whichever end is nearer that edge carries it.
                        bool vertical = Math.Abs(e1.Y - e0.Y) >= Math.Abs(e1.X - e0.X);
                        var at = vertical ? (e0.Y <= e1.Y ? e0 : e1) : (e0.X <= e1.X ? e0 : e1);
                        // Clamped to the band the burn-in can actually use BEFORE the overlap test,
                        // or two labels that only collide after clamping both survive the test and
                        // then print on top of each other.
                        int lx = Math.Clamp(at.X + 4, 2, Math.Max(2, sizeW - 40));
                        int ly = Math.Clamp(at.Y + 3, 2, Math.Max(2, sizeH - 30));
                        // A line entering at a CORNER lands its label in the other family's row, and
                        // two numbers printed on top of each other are worse than one number: the
                        // reader cannot tell which axis either belongs to. First placed wins; the
                        // dropped line still has its neighbours a spacing away to interpolate from.
                        float boxW = label.Length * 12f * (sizeH / 1280f) * 0.62f + 6f;
                        float boxH = 14f * (sizeH / 1280f) + 3f;
                        foreach (var (_, ox, oy) in gridLabels)
                            if (MathF.Abs(lx - ox) < boxW && MathF.Abs(ly - oy) < boxH) return;
                        gridLabels.Add((label, lx, ly));
                    }

                    // The grid's own legend, burned in the picture with the numbers it explains
                    // rather than in the caption bar: spacing and PLANE together, because a
                    // coordinate read off a grid ruled at the wrong storey is wrong, and the only
                    // defence is saying which storey it was ruled at. Placed BEFORE the lines so a
                    // line label that would land on it is the one dropped — the legend is the key
                    // to every other number in the picture and outranks any single one of them.
                    gridLabels.Add(($"grid {gridSpacing:0.##} u  ·  ruled at z {grid.Z:0} ({grid.ZWhy})", 6, sizeH - 24));
                    for (int i = nx0; i <= nx1; i++)
                    {
                        float x = i * gridSpacing;
                        RuleLine(new Vector3(x, minY, grid.Z), new Vector3(x, maxY, grid.Z), $"X {x:0.##}");
                    }
                    for (int i = ny0; i <= ny1; i++)
                    {
                        float y = i * gridSpacing;
                        RuleLine(new Vector3(minX, y, grid.Z), new Vector3(maxX, y, grid.Z), $"Y {y:0.##}");
                    }
                    Console.WriteLine($"   grid: {nx1 - nx0 + 1} X line(s) and {ny1 - ny0 + 1} Y line(s) every {gridSpacing:0.##} u"
                                    + (grid.Spacing is null ? " (auto)" : "")
                                    + $", ruled on the plane z {grid.Z:0} ({grid.ZWhy}); frame covers "
                                    + $"{minX:0},{minY:0} .. {maxX:0},{maxY:0} ({span:0} u across)");
                }
            }
        }

        // ---- the occlusion preview, applied BEFORE anything is drawn ----------------------------
        // Pass 1's depth prepass, the adaptive colour sample and the picture must all see the same
        // scene, so a culled ref comes off (or is tinted) here, once, for the entry eye. The
        // fly-through takes the state instead and re-applies it as the eye moves. The caption
        // names the count: a render with objects missing must say why, or the gap reads as a
        // finding about the mod rather than about its planes.
        var capLines = captionLines.ToList();
        // The report reads the answer for the entry eye and nothing is pushed — the preview below
        // recomputes for itself, canary and all, so the two cannot contaminate each other.
        if (planeReport is not null)
        {
            occl.Evaluate(cam.Eye);
            if (occl.Refs.Any(r => r.Issue is not null || r.Parts.Any(s => s.Issue is not null)))
                occl.SceneIssues.Add("missing or unsupported scene geometry; visible pixels describe the rendered scene only");
            if (cullAbove is not null) occl.SceneIssues.Add("height clipping changes occluding geometry");
            if (stateView.BuiltMode == "diff") occl.SceneIssues.Add("state diff combines two populations and is not a gameplay state");
            if (npcActors.Count > 0) occl.SceneIssues.Add("posed NPCs are rendered but their engine culling bounds are unsupported");
            if (occl.Refs.Any(r => r.Ref.BosRandom)) occl.SceneIssues.Add("random BOS state is not pinned to a measured game state");
            // Both depth passes use the same single-sample FBO. Resolving MSAA depth can select
            // different covered samples when the rest of the scene is removed. Off-mode colour
            // rendering still uses its original MSAA path after every saved visibility flag is restored.
            if (interactive is null)
            {
                var plainDepth = new float[sizeW * sizeH];
                Draw(plainDepth, singleSample: true);
                string DepthHash(float[] depth) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(depth.AsSpan())));
                occl.SceneDepthHash = DepthHash(plainDepth);
                var evidenceDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!,
                    Path.GetFileNameWithoutExtension(outPath) + "-audit-evidence");
                if (Directory.Exists(evidenceDir)) throw new System.IO.IOException("Refusing to replace audit evidence: " + evidenceDir);
                Directory.CreateDirectory(evidenceDir);
                occl.SceneDepthPath = Path.Combine(evidenceDir, "scene-depth-f32.bin");
                File.WriteAllBytes(occl.SceneDepthPath, System.Runtime.InteropServices.MemoryMarshal.AsBytes(plainDepth.AsSpan()).ToArray());
                var allMeshes = meshes.Concat(npcActors.SelectMany(a => a.Meshes)).Distinct().ToArray();
                var savedVisibility = allMeshes.ToDictionary(m => m, m => m.IsRendering);
                var ownDepth = new float[sizeW * sizeH];
                occl.MarkVisible(part =>
                {
                    if (occl.VisibilityDraws >= occl.VisibilityBudget)
                        return new ShapeVisibility(0, 0, 0, "explicit per-view visibility draw budget exhausted");
                    if (part.Meshes.Count == 0) return new ShapeVisibility(0, 0, 0, "no shape geometry to rasterize");
                    try
                    {
                        var isolated = part.Meshes.ToHashSet();
                        foreach (var m in allMeshes) m.IsRendering = savedVisibility[m] && isolated.Contains(m);
                        Draw(ownDepth, singleSample: true);
                        occl.VisibilityDraws++;
                        part.IsolatedDepthHash = DepthHash(ownDepth);
                        var measured = ShapeVisibility.Compare(ownDepth, plainDepth);
                        var mask = new byte[sizeW * sizeH * 4];
                        for (int i = 0; i < ownDepth.Length; i++)
                        {
                            mask[i*4+3] = 255;
                            if (ownDepth[i] == 1) continue;
                            mask[i*4 + (ownDepth[i] <= plainDepth[i] ? 1 : 0)] = 255;
                        }
                        part.VisibilityMask = Path.Combine(evidenceDir, $"shape-{occl.VisibilityDraws:D4}-block-{part.Block}.png");
                        using var maskImage = Image.LoadPixelData<Rgba32>(mask, sizeW, sizeH);
                        maskImage.Mutate(x => x.Flip(FlipMode.Vertical));
                        maskImage.SaveAsPng(part.VisibilityMask);
                        return measured;
                    }
                    finally { foreach (var (m, visible) in savedVisibility) m.IsRendering = visible; }
                });
            }
            else occl.SceneIssues.Add("interactive view has no captured shape visibility measurement");
            planeReport(occl);
        }
        if (occlusionMode != "off")
        {
            if (planes.Count == 0)
                Console.WriteLine("   occlusion preview: no occlusion planes in this cell — nothing to cull");
            else if (interactive is null)
            {
                occl.Apply(cam.Eye, occlusionMode, ownsVisibility: true);
                Console.WriteLine("   " + occl.Summary(occlusionMode));
                // How many of the culled refs are even in the frame: a preview that changes no
                // pixel is the honest outcome for well-placed planes (what they cull is behind a
                // wall anyway), and this line is what separates that from a preview that failed.
                int inFrame = occl.Refs.Count(r => r.Culled && ScreenOf(YUp((r.Min + r.Max) * 0.5f)) is not null);
                Console.WriteLine($"      {inFrame:N0} predicted-culled ref centre(s) are inside this frame; this is not a shape visibility test");
                foreach (var kv in occl.CulledByPlane.OrderByDescending(kv => kv.Value.Count).Take(12))
                    Console.WriteLine($"      {kv.Value.Count,5} culled by PLANE {kv.Key.Key} {kv.Key.KindLabel} {kv.Key.ExtentLabel} ({kv.Key.ProvenanceLabel})"
                                    + (occl.StraddlingByPlane.TryGetValue(kv.Key, out var st) ? $", {st} straddling" : ""));
                if (occl.CulledByPlane.Count > 12) Console.WriteLine($"      ... and {occl.CulledByPlane.Count - 12} more plane(s) culling something");
                capLines.Add($"OCCLUSION RESEARCH {occlusionMode}: {occl.Refs.Sum(r => r.Parts.Count(s => s.Culled)):N0} shape(s) in {occl.CulledCount:N0} ref(s) predicted culled — "
                           + (occlusionMode == "on" ? "affected shapes NOT DRAWN" : "affected shapes tinted RED")
                           + "; inspect report coverage");
            }
        }
        // The BOS paint, before anything is drawn (the static frame) — the window re-applies it on B.
        if (bosView.BuiltMode != "off" && bosView.SwappedCount > 0)
        {
            bosView.Apply(bosMode);
            Console.WriteLine("   " + bosView.Summary());
        }
        // The state paint, after BOS (a mesh both touch takes the last applied) — the window re-applies it on T.
        if (stateName.Length > 0 && stateView.Count > 0)
        {
            stateView.Apply(stateMode);
            Console.WriteLine("   " + stateView.Summary());
        }
        if (interactive is not null)
        {
            interactive.Occlusion = occl;
            interactive.OcclusionMode = planes.Count > 0 ? occlusionMode : "off";
            interactive.Bos = bosView;
            interactive.State = stateName.Length > 0 ? stateView : null;
        }

        Vector4 healthy = palette[0].Color;
        bool autoColour = wantNav && navColor == "auto";
        if (!autoColour)
        {
            var pick = palette.FirstOrDefault(p => p.Name == navColor);
            healthy = pick.Name is null ? palette[0].Color : pick.Color;
        }

        // Pass 1 — the world with NO overlay on it. Its pixels choose the healthy colour; its
        // DEPTH is what separates "behind a rock" from "in plain view". Skipped when neither is
        // wanted, so a forced colour with the dashes off costs nothing. The finding is outlined
        // in EVERY mode, so its dashes need the depth even when the healthy mesh is a sheet.
        bool wantDepth = wantNav && (wireMode || redTris.Count > 0) && dashUnderStatics;
        float[]? sceneDepth = null;
        if (wantNav && (autoColour || wantDepth))
        {
            if (wantDepth) sceneDepth = new float[sizeW * sizeH];
            var bg = Draw(sceneDepth);
            if (autoColour && navTris.Count > 0)
            {
                float sr = 0, sg = 0, sb = 0; int hits = 0;
                int stride = Math.Max(1, navTris.Count / 600);
                for (int i = 0; i < navTris.Count; i += stride)
                {
                    var (a, b, c) = navTris[i];
                    if (ScreenOf(YUp((a + b + c) / 3f + lift)) is not { } sp) continue;
                    int o = (sp.Y * sizeW + sp.X) * 4;
                    sr += bg[o] / 255f; sg += bg[o + 1] / 255f; sb += bg[o + 2] / 255f; hits++;
                }
                if (hits > 0)
                {
                    var mean = new Vector3(sr / hits, sg / hits, sb / hits);
                    // The route's colour is RESERVED, and the reservation has to reach the scorer or
                    // it is not one: a route drawn in cyan over a mesh the sampler also decided was
                    // best in cyan is the failure this exists to prevent, and it makes the WHOLE
                    // overlay unreadable rather than one layer of it. Near matches are struck out
                    // outright and the rest are scored away from it — unless that would leave the
                    // sampler nothing to choose, in which case contrast with the ground still wins.
                    // The GRID is reserved on exactly the same terms and for exactly the same
                    // reason: it is ruled across the whole frame, so a grid that lands on the
                    // navmesh's hue is worse than a route that does.
                    // EVERY route's colour is reserved, not just the first: a shot carrying a dozen
                    // measured paths in three colours has three hues the mesh may not take.
                    var gridC = gridSpacing > 0f ? gridColour3 : (Vector3?)null;
                    // The planes layer's provenance colours are reserved on the same terms, but
                    // only the ones actually drawn — a run with no moved plane has no teal in it.
                    var planeCs = showPlanes ? planes.Select(p => (Vector3?)p.Colour) : Array.Empty<Vector3?>();
                    var reserved = routes.Select(rt => (Vector3?)new Vector3(rt.Color.X, rt.Color.Y, rt.Color.Z)).Append(gridC).Concat(planeCs)
                                         .Where(c => c is not null).Select(c => c!.Value).Distinct().ToArray();
                    const float RouteHueGuard = 0.35f;
                    var usable = reserved.Length > 0
                               ? palette.Where(pe => reserved.All(rv => (new Vector3(pe.Color.X, pe.Color.Y, pe.Color.Z) - rv).Length() >= RouteHueGuard)).ToArray()
                               : palette;
                    bool struckOut = usable.Length < palette.Length;
                    if (usable.Length == 0) { usable = palette; struckOut = false; }
                    float best = float.MinValue; string chosen = usable[0].Name;
                    foreach (var (name, colr) in usable)
                    {
                        var c3 = new Vector3(colr.X, colr.Y, colr.Z);
                        // Contrast with what is under the mesh, plus distance from the reserved red
                        // and from every other reserved colour drawn in this frame.
                        float score = (c3 - mean).Length() + 0.5f * (c3 - new Vector3(red.X, red.Y, red.Z)).Length();
                        foreach (var rv in reserved) score += 0.5f * (c3 - rv).Length();
                        if (score > best) { best = score; chosen = name; healthy = colr; }
                    }
                    Console.WriteLine($"   nav colour: auto -> {chosen} (sampled {hits} points under the mesh, "
                                    + $"background {(int)(mean.X * 255)},{(int)(mean.Y * 255)},{(int)(mean.Z * 255)})"
                                    + (struckOut ? $"; {palette.Length - usable.Length} entr(ies) struck out as too close to the reserved route/grid colour(s)" : ""));
                }
                else
                    Console.WriteLine("   nav colour: auto -> green (mesh not on screen; nothing to sample)");
            }
            else if (autoColour)
                Console.WriteLine("   nav colour: auto -> green (no healthy navmesh to sample under)");
        }

        var healthyGroups = navColourGroups ?? new[] { new NavColourGroup("default", navTris) };
        Vector4 GroupColour(NavColourGroup group) => group.Colour == "default" ? healthy
            : palette.Single(p => p.Name == group.Colour).Color;

        // The translucent sheet still goes through the library renderer — it is surface, not
        // annotation, and it is supposed to be hidden by whatever covers it.
        if (wantNav && fillMode)
        {
            // The sheet's alpha is baked into its vertex colours, so it cannot be re-tuned once
            // uploaded. Under --interactive it is therefore baked at the FILLED value and the
            // loop scales MaterialAlpha to land on whichever mode's pixels are wanted (the
            // shader multiplies the two, so "both" is not 0.16 of this sheet but 0.16²/0.35 —
            // see InteractiveViewer.FillAlpha).
            float alpha = interactive is not null ? 0.35f : navMode == "both" ? 0.16f : 0.35f;
            foreach (var group in healthyGroups.Where(g => g.Triangles.Count > 0))
            {
                var colour = GroupColour(group);
                var m0 = BuildFill(group.Triangles, new Vector4(colour.X, colour.Y, colour.Z, alpha), "navmesh-fill");
                m0.IsDecal = true;             // same insertion-ordered channel, added after the ground
                renderer.AddMesh(m0); meshes.Add(m0);
                interactive?.NavFill.Add(m0);
            }
            if (redTris.Count > 0)
            {
                var m1 = BuildFill(redTris, new Vector4(0.95f, 0.15f, 0.08f, MathF.Min(0.55f, alpha + 0.2f)), "navmesh-fill-red");
                m1.IsDecal = true;
                renderer.AddMesh(m1); meshes.Add(m1);
                interactive?.NavFillRed.Add(m1);
            }
        }

        // ---- NPC pucks: the translucent fill disc ----------------------------------------
        // A standing figure is a few dozen pixels from a top-down camera; the puck is what
        // makes "the NPC is HERE" legible at plan scale (Piranha's "puck tracker" request).
        // The FILL rides the decal channel like the navmesh sheet — surface, depth-tested,
        // hidden by what covers it — and joins after Pass 1 so the adaptive nav colour never
        // samples its own marker yellow. The always-visible RIM is overlay geometry, added
        // with the other layers below.
        if (npcPucks && npcActors.Count > 0)
        {
            const int PuckSegments = 48;
            foreach (var actor in npcActors)
            {
                var c = actor.PositionSkyrim;
                var discTris = new List<(Vector3 A, Vector3 B, Vector3 C)>(PuckSegments);
                for (int s = 0; s < PuckSegments; s++)
                {
                    float a0 = 2f * MathF.PI * s / PuckSegments, a1 = 2f * MathF.PI * (s + 1) / PuckSegments;
                    discTris.Add((c,
                        c + new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0f) * actor.PuckRadius,
                        c + new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0f) * actor.PuckRadius));
                }
                var disc = BuildFill(discTris,
                    new Vector4(actor.PuckColor.X, actor.PuckColor.Y, actor.PuckColor.Z, 0.38f),
                    $"npc-puck:{actor.Label}");
                disc.IsDecal = true;
                renderer.AddMesh(disc); meshes.Add(disc);
            }
        }

        // ---- the wire, classified step by step ------------------------------------------
        const int Visible = 0, Subterranean = 1, Hidden = 2;
        int runVisible = 0, runUnder = 0, runHidden = 0;

        float occlRel = 1.002f, occlAbs = 1.5f;
        if (Environment.GetEnvironmentVariable("VP_OCCL_SLACK") is { } slackSpec)
        {
            var sp2 = slackSpec.Split(',');
            if (sp2.Length == 2 && float.TryParse(sp2[0], out var rr) && float.TryParse(sp2[1], out var aa))
            { occlRel = rr; occlAbs = aa; Console.WriteLine($"   occlusion slack: x{occlRel} + {occlAbs} u (from VP_OCCL_SLACK)"); }
        }

        // Window depth -> distance along the view axis, so the compare happens in world units
        // instead of in the wildly non-linear depth encoding a 4..131072 frustum produces.
        static float ViewDist(float winZ)
        {
            float zn = winZ * 2f - 1f;
            return 2f * NearPlane * FarPlane / (FarPlane + NearPlane - zn * (FarPlane - NearPlane));
        }

        int Classify(Vector3 pSkyrim)                 // point already carries the lift
        {
            if (terrainHeightAt is not null && terrainHeightAt(pSkyrim.X, pSkyrim.Y) is { } tz
                && pSkyrim.Z < tz - 0.5f)
                return Subterranean;
            if (sceneDepth is null) return Visible;
            var pY = YUp(pSkyrim);
            if (ScreenOf(pY) is not { } sp) return Visible;          // off-frame: nothing hides it
            float scene = ViewDist(sceneDepth[sp.Y * sizeW + sp.X]);
            float mine = Vector3.Dot(pY - eyeY, viewFwd);
            // Slack, and only as much as the numbers actually need. A 24-bit depth buffer over a
            // 4..131072 frustum resolves ~0.06 world units at 2,000 out, so 2% + 3 u was ~40 u of
            // blind zone there: an edge hidden by a mound but LESS than 40 u behind its surface
            // classified "visible", got drawn solid, and was then clipped by that same mound —
            // vanishing entirely instead of dashing. The navmesh rides 2 u above its own ground
            // and is nearer than it along every view ray, so self-occlusion needs no more than
            // this. Env VP_OCCL_SLACK="rel,abs" overrides both for diagnosis.
            return mine > scene * occlRel + occlAbs ? Hidden : Visible;
        }

        // One edge -> solid runs + dashed runs, in Y-up viewer space, 6 floats a vertex
        // (pos3 + colour3). Step and dash length both track distance, so classification stays
        // ~6 px fine and a dash reads the same size whether the mesh is 200 or 2,000 units out.
        void EmitEdge(Vector3 p, Vector3 q, Vector3 nrm, Vector3 colour, float halfWidth,
                      List<float> solid, List<float> dash)
        {
            var d = q - p;
            float len = d.Length();
            if (len < 1e-4f) return;
            var dir = d / len;
            var sideRaw = Vector3.Cross(nrm, dir);
            if (sideRaw.LengthSquared() < 1e-8f) sideRaw = Vector3.UnitZ;
            var side = YUp(Vector3.Normalize(sideRaw) * halfWidth);

            float camLen = MathF.Max(1f, Vector3.Distance((p + q) * 0.5f, cam.Eye));
            float step = Math.Clamp(camLen * 0.006f, 3f, 96f);
            float dashOn = Math.Clamp(camLen * 0.011f, 3f, 400f);
            float period = dashOn * 1.85f;
            int steps = Math.Clamp((int)MathF.Ceiling(len / step), 1, 512);
            float seg = len / steps;

            void Ribbon(List<float> dst, float t0, float t1)
            {
                var a = YUp(p + dir * t0); var b = YUp(p + dir * t1);
                // A ZERO normal tells the overlay shader "flat, exactly this colour", so every
                // line keeps the colour it had before the spheres needed shading.
                void V(Vector3 v) => Overlay.Vertex(dst, v, colour, Vector3.Zero);
                V(a - side); V(a + side); V(b + side);
                V(a - side); V(b + side); V(b - side);
            }
            void Run(int cls, float t0, float t1)
            {
                if (t1 - t0 < 1e-4f) return;
                if (cls == Visible) runVisible++; else if (cls == Subterranean) runUnder++; else runHidden++;

                // THE DEPTH BUFFER IS THE ARBITER, NOT THE CLASSIFIER.
                //
                // Every segment gets a SOLID, depth-tested copy — so it draws wherever it is
                // genuinely visible and is clipped wherever it is not, per pixel, exactly. Every
                // segment whose category is dashed also gets an always-visible DASHED copy, which
                // is precisely what remains where the solid one was clipped. Over a segment that
                // really is visible the dashes land on the solid line, same colour, same width,
                // and change nothing.
                //
                // With both dash categories on — the default — the classification therefore has
                // NO effect on the picture at all: solid where visible, dashed where hidden, and
                // the GPU decides which. That is what makes it impossible for a segment to
                // disappear, which is what a classifier deciding visibility on its own kept doing:
                // any slack at all is a blind zone, and a rug, a plank edge or a shallow rise all
                // fit inside one. The classification now only names the CATEGORY, for the two
                // toggles and for the counts.
                Ribbon(solid, t0, t1);
                bool dashed = cls == Visible
                    ? dashUnderground || dashUnderStatics       // may be hidden after all: cover it
                    : (cls == Subterranean && dashUnderground) || (cls == Hidden && dashUnderStatics);
                if (!dashed) return;
                // Phase the dashes off the edge start, not off the run, so consecutive runs of
                // the same edge join into one continuous dash pattern.
                for (float t = t0 - (t0 % period); t < t1; t += period)
                {
                    float a = MathF.Max(t, t0), b = MathF.Min(t + dashOn, t1);
                    if (b > a) Ribbon(dash, a, b);
                }
            }

            int runCls = Classify(p + dir * (seg * 0.5f));
            float runStart = 0f;
            for (int i = 1; i <= steps; i++)
            {
                int cls = i < steps ? Classify(p + dir * (seg * (i + 0.5f))) : -1;
                if (cls == runCls) continue;
                Run(runCls, runStart, seg * i);
                runCls = cls; runStart = seg * i;
            }
        }

        (List<float> Solid, List<float> Dash) BuildWire(
            IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> tris, Vector4 colour4, float halfWidth)
        {
            var solid = new List<float>(); var dash = new List<float>();
            var colour = new Vector3(colour4.X, colour4.Y, colour4.Z);
            foreach (var (a0, b0, c0) in tris)
            {
                var a = a0 + lift; var b = b0 + lift; var c = c0 + lift;
                var n = Vector3.Cross(b - a, c - a);
                n = n.LengthSquared() > 1e-10f ? Vector3.Normalize(n) : Vector3.UnitZ;
                EmitEdge(a, b, n, colour, halfWidth, solid, dash);
                EmitEdge(b, c, n, colour, halfWidth, solid, dash);
                EmitEdge(c, a, n, colour, halfWidth, solid, dash);
            }
            return (solid, dash);
        }

        // Draw order is the precedence order: healthy solid, healthy dashes, then the finding
        // over both. A layer flagged false skips the depth test — that IS the "always visible"
        // part of a dashed line.
        // Data, whether it depth-tests, and whether back faces are culled. Culling matters for
        // exactly one thing: a solid drawn WITHOUT a depth test paints whichever of its faces the
        // buffer happened to receive last, so a sphere shows its far hemisphere and shades as if
        // lit from behind. Culling the back faces makes the near hemisphere the only candidate.
        // Ribbons are single-sided-by-accident and must NOT be culled.
        // Nav tags the layer so the interactive overlay pass can drop the navmesh wire without
        // touching anything else on it: 0 = not navmesh, 1 = healthy wire, 2 = the finding's
        // wire, 3 = the route (R toggles it, same idea, its own key). The static path never
        // reads it — its mode was decided before the build.
        // Alpha is 1 for every classic layer and the draw loop's behaviour at 1 is exactly what it
        // was before the field existed (blend off, u_alpha 1). Only the route's waypoint discs ask
        // for less: a translucent fill marks a spot without hiding the ground it marks.
        var layers = new List<(float[] Data, bool DepthTested, bool CullBack, int Nav, float Alpha)>();
        if (refSurface)
        {
            // The wording at opacity 1 is load-bearing and deliberately byte-identical: the
            // contact gate (../subject_gate.py) reads this exact line to qualify a capture as
            // OPAQUE identity evidence. A translucent mark is a presentation choice, not that
            // evidence, so it reports itself differently and the gate refuses it by construction.
            Console.WriteLine(refSurfaceOpacity >= 1f
                ? "subject-surface-mode: exact placed opaque triangles; depth-tested; no inflated hull or occluded ghost"
                : $"subject-surface-mode: exact placed triangles at opacity {refSurfaceOpacity:0.###} over the subject's own material; "
                  + "depth-tested; no inflated hull or occluded ghost; NOT opaque identity evidence");
            foreach (var key in subjectSurfaceRefusals.OrderBy(x => x, StringComparer.Ordinal))
                Console.WriteLine($"subject-surface-refused: {key}: alpha, effect, marker or non-depth-writing shape; participant coverage incomplete");
            if (subjectSurface.Count > 0) layers.Add((subjectSurface.ToArray(), true, true, 0, refSurfaceOpacity));
            if (subjectSurfaceDoubleSided.Count > 0) layers.Add((subjectSurfaceDoubleSided.ToArray(), true, false, 0, refSurfaceOpacity));
        }
        // Highlight halos: the SAME hull buffer is drawn twice — solid depth-tested rim here,
        // then again as the translucent Greater-pass ghost (see the draw loop).
        float[]? haloData = haloTris.Count > 0 ? haloTris.ToArray() : null;
        if (haloData is not null) layers.Add((haloData, true, true, 0, 1f));
        // The grid goes on FIRST, so every later layer paints over it: it is the ruling under the
        // picture, and a scale that hid the navmesh or the route it exists to measure would have
        // the priority exactly backwards. Depth test OFF — a reference line you can only see on
        // open floor is not a reference; it has to cross the pillars too.
        if (gridSpacing > 0f && gridRibbon.Count > 0) layers.Add((gridRibbon.ToArray(), false, false, 0, 0.85f));
        // Caller-drawn ribbons (the seam overlay): depth-tested, so a seam behind a hill stays behind it — the
        // caller lifts each ribbon a few units above the ground it marks, which is what keeps it on top of the terrain.
        if (overlayLines.Count > 0)
        {
            var ribbon = new List<float>();
            foreach (var ol in overlayLines)
            {
                float w = ol.Width;
                Overlay.SolidPolyline(ribbon, ol.Pts, ol.Colour, d => Math.Clamp(d * 0.0012f * w, 0.5f * w, 20f * w), cam.Eye);
            }
            if (ribbon.Count > 0) layers.Add((ribbon.ToArray(), true, false, 0, 1f));
        }
        if (wantNav)
        {
            if (wireMode && navTris.Count > 0)
            {
                foreach (var group in healthyGroups)
                {
                    var (s, d) = BuildWire(group.Triangles, GroupColour(group), 2.2f);
                    if (s.Count > 0) layers.Add((s.ToArray(), true, false, 1, 1f));
                    if (d.Count > 0) layers.Add((d.ToArray(), false, false, 1, 1f));
                }
            }
            // The finding is ALWAYS outlined, wider than the healthy wire, in every mode that
            // draws navmesh at all.
            if (redTris.Count > 0)
            {
                var (s, d) = BuildWire(redTris, red, 3.4f);
                if (s.Count > 0) layers.Add((s.ToArray(), true, false, 2, 1f));
                if (d.Count > 0) layers.Add((d.ToArray(), false, false, 2, 1f));
            }
            if (runVisible + runUnder + runHidden > 0)
            {
                string dashing = dashUnderground && dashUnderStatics ? "underground + behind-geometry"
                               : dashUnderground ? "underground only"
                               : dashUnderStatics ? "behind-geometry only" : "nothing (dashes off)";
                Console.WriteLine($"   nav wire: {runVisible:N0} visible run(s), {runUnder:N0} subterranean, "
                                + $"{runHidden:N0} behind geometry; dashed: {dashing}"
                                + (dashUnderground && dashUnderStatics
                                   ? "  (counts describe the classifier; the depth buffer decides the picture)" : ""));
            }
        }

        // ---- the target cell's footprint ------------------------------------------------
        // Terrain is drawn for the target cell AND its ring of neighbours, and nothing in the
        // picture says where one stops and the other starts. A white dashed line along the four
        // edges, riding the ground, draws that boundary: inside it is the cell being examined,
        // outside it is context. Always visible — a boundary you can only see on open flat ground
        // is not a boundary you can navigate by.
        if (cellBounds is { } cb && terrainHeightAt is not null)
        {
            var outline = new List<float>();
            var white = new Vector3(1f, 1f, 1f);
            void Edge(float ax, float ay, float bx, float by)
            {
                // ~64 u sampling: fine enough to hug a hillside, coarse enough to stay cheap.
                int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(bx - ax), MathF.Abs(by - ay)) / 64f));
                var pts = new List<Vector3>(steps + 1);
                for (int i = 0; i <= steps; i++)
                {
                    float f = (float)i / steps;
                    float x = ax + (bx - ax) * f, y = ay + (by - ay) * f;
                    float z = terrainHeightAt(x, y) ?? terrainHeightAt(ax, ay) ?? 0f;
                    pts.Add(new Vector3(x, y, z + 6f));
                }
                Overlay.DashedPolyline(outline, pts, white, 3.0f, cam.Eye);
            }
            Edge(cb.X0, cb.Y0, cb.X1, cb.Y0);
            Edge(cb.X1, cb.Y0, cb.X1, cb.Y1);
            Edge(cb.X1, cb.Y1, cb.X0, cb.Y1);
            Edge(cb.X0, cb.Y1, cb.X0, cb.Y0);
            if (outline.Count > 0)
            {
                layers.Add((outline.ToArray(), false, false, 0, 1f));
                Console.WriteLine($"   cell outline: white dashes around {cb.X0:0},{cb.Y0:0} .. {cb.X1:0},{cb.Y1:0}");
            }
        }

        // ---- the diff: navmesh vertices unique to this panel's version -------------------
        // A re-cut navmesh moves vertices, and spotting that between two wire drawings is exactly
        // what the eye is worst at. Spheres at the vertices THIS version has and the other one
        // does not turn "spot the difference" into "look at the marked points". Always visible,
        // and shaded — the only lit thing in the overlay — so they read as objects, not blobs.
        if (diffVerts.Count > 0)
        {
            var spheres = new List<float>();
            var dc = new Vector3(diffColor.X, diffColor.Y, diffColor.Z);
            foreach (var v in diffVerts)
            {
                float d = MathF.Max(1f, Vector3.Distance(v, cam.Eye));
                Overlay.Sphere(spheres, YUp(v + lift), Math.Clamp(d * 0.00275f, 1f, 45f), dc);
            }
            layers.Add((spheres.ToArray(), false, true, 0, 1f));
            Console.WriteLine($"   nav diff: {diffVerts.Count} vertex sphere(s) — vertices this version has and the other does not");
        }

        // ---- NPC pucks: the always-visible rim -------------------------------------------
        // A thin yellow ring at the fill disc's edge, depth test OFF like the dashed layers:
        // an actor occluded by a mezzanine or a wall still announces where it stands. The
        // rim is the findability half; the translucent fill (a decal, added above) is the
        // ground-footprint half and stays honestly hidden by whatever covers it.
        if (npcPucks && npcActors.Count > 0)
        {
            const int RimSegments = 64;
            var rim = new List<float>();
            foreach (var actor in npcActors)
            {
                var c = actor.PositionSkyrim + new Vector3(0, 0, 2.5f);
                float rIn = actor.PuckRadius - 1.75f, rOut = actor.PuckRadius + 1.75f;
                // Pre-divide by the overlay shader's x1.5 lift so the rim lands at the
                // actor's EXACT identity colour — the same one the fill and the caption
                // line wear. (All palette entries are <= 1, so /1.5 then x1.5 round-trips
                // without hitting the clamp; the old always-yellow rim relied on the lift
                // and came out a step brighter than its own fill.)
                var rimColour = actor.PuckColor / 1.5f;
                for (int s = 0; s < RimSegments; s++)
                {
                    float a0 = 2f * MathF.PI * s / RimSegments, a1 = 2f * MathF.PI * (s + 1) / RimSegments;
                    var d0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0f);
                    var d1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0f);
                    var i0 = YUp(c + d0 * rIn); var o0 = YUp(c + d0 * rOut);
                    var i1 = YUp(c + d1 * rIn); var o1 = YUp(c + d1 * rOut);
                    Overlay.Vertex(rim, i0, rimColour, Vector3.Zero);
                    Overlay.Vertex(rim, o0, rimColour, Vector3.Zero);
                    Overlay.Vertex(rim, o1, rimColour, Vector3.Zero);
                    Overlay.Vertex(rim, i0, rimColour, Vector3.Zero);
                    Overlay.Vertex(rim, o1, rimColour, Vector3.Zero);
                    Overlay.Vertex(rim, i1, rimColour, Vector3.Zero);
                }
            }
            layers.Add((rim.ToArray(), false, false, 0, 1f));
            Console.WriteLine($"   npc pucks: {npcActors.Count} disc(s) + always-visible rim(s), one identity colour per actor");
        }

        // ---- the route: where to WALK ----------------------------------------------------
        // Straight segments waypoint to waypoint, arrowheads riding them that say which way. All
        // of it lies FLAT ON THE GROUND, at the height the winning navmesh gave — painted on the
        // floor rather than billboarded at the camera, because the thing being shown is a path
        // over ground, and a path that floats free of the ground is a decoration.
        //
        // A leg is STRAIGHT IN PLAN and follows the ground in z: the caller hands over each
        // authored segment already densified into a ground-riding polyline. Straight in 3D was
        // the obvious thing and is wrong — a chord from the foot of an arched walkway to its
        // crest runs ~50 u UNDER the walkway, and once waypoints sit only where the direction
        // changes there is no longer a waypoint every 250 u hiding it.
        //
        // The segments go through EmitEdge, so they inherit the nav wire's whole treatment for
        // free: a solid depth-tested copy plus an always-visible dashed one. That is what keeps
        // the route readable where it passes behind a wall, and it is why this needed no new
        // machinery. Arrowheads are always-visible flat triangles — a direction you can only see
        // on open ground is not a direction anyone can follow.
        foreach (var route in routes)
        {
            if (route.Legs.Count == 0) continue;
            // Above the nav sheet's own 2 u lift: the route is the thing being followed, and it
            // must not z-fight with the surface it is drawn over.
            var routeLift = new Vector3(0, 0, 4f);
            // Pre-divided by the overlay shader's x1.5 flat lift, exactly as the NPC rim is, so
            // the drawn path lands on the EXACT colour its caption line is written in.
            var rc = new Vector3(route.Color.X, route.Color.Y, route.Color.Z) / 1.5f;
            var rSolid = new List<float>(); var rDash = new List<float>();
            var rFlat = new List<float>();          // arrowheads: always visible, opaque
            var rDisc = new List<float>();          // waypoint fills: always visible, translucent
            int v0 = runVisible, u0 = runUnder, h0 = runHidden;
            int heads = 0, pathPoints = 0;

            // ONE width law for the whole route, and the arrowhead is sized FROM IT rather than
            // from a formula of its own. d x 0.0020 is a line ~5 px wide at any distance; the
            // clamps exist only to stop a ribbon degenerating at the extremes, and BOTH of them
            // cost a round when they were set carelessly. Two independent clamps, one for the line
            // and one for the head, let the head come out barely wider than the line it rode, so
            // the arrows read as nicks in a band; and a generous LOW clamp stops being a floor and
            // becomes the width of every near segment, so a route at the camera's feet drew as a
            // band wider than its own arrows. Retune the law, never one of its two consumers.
            static float HalfWidthAt(float d) => Math.Clamp(d * 0.0020f, 0.5f, 36f);
            // Everything about the arrow is its own knob, in half-widths, because every time one of
            // these was expressed in terms of another the other one moved when it should not have.
            // Twice now: the head was once sized by its own distance clamp and came out as wide as
            // the line it rode; and the half-span was once a fraction that dragged the width along
            // whenever the length changed. Half-span and notch are still fractions OF THE LENGTH —
            // that is what holds the head's shape as it scales — so changing the length means
            // changing them to compensate if the width is meant to stay put. It was, twice.
            const float HeadLengthInWidths = 34f;   // half the 68 it was; 0.20 and 0.41 below keep
            const float HeadHalfSpan = 0.20f;       // the head's WIDTH and notch depth unchanged
            const float HeadNotch = 0.41f;          // (34 x 0.20 == 68 x 0.10, and likewise the notch)
            // Spacing is in half-widths TOO, not in head lengths. Derived from the head it would
            // halve when the head halved, holding the head-to-gap ratio exactly where it was and
            // leaving a shortened arrowhead just as cluttered as the long one.
            const float ArrowEveryInWidths = 92f;

            // One arrowhead: a barbed pair of triangles in the ground plane, sized from ITS OWN
            // distance to the camera — the same trick the dashes use — so every head along the
            // path reads the same size on screen instead of shrinking into the distance. The
            // direction is the LOCAL one, so a head on a ramp lies in the ramp.
            void Arrow(Vector3 at, Vector3 dir)
            {
                // The perpendicular has to lie IN THE GROUND PLANE for a flat arrow. A vertical
                // step has no such perpendicular; +X stands in rather than emitting nothing.
                var side = Vector3.Cross(Vector3.UnitZ, dir);
                side = side.LengthSquared() > 1e-8f ? Vector3.Normalize(side) : Vector3.UnitX;
                float d = MathF.Max(1f, Vector3.Distance(at, cam.Eye));
                float head = HalfWidthAt(d) * HeadLengthInWidths;
                var tip = at + dir * (head * 0.5f);
                var back = at - dir * (head * 0.5f);
                // The rear notch is what separates an arrow from a wedge. It is kept SHALLOW —
                // barbs reaching far behind it read as a dart — and its depth is a fraction of the
                // length like everything else, so it holds its shape as the head grows.
                var notch = at - dir * (head * HeadNotch);
                var bl = back + side * (head * HeadHalfSpan);
                var br = back - side * (head * HeadHalfSpan);
                void V(Vector3 v) => Overlay.Vertex(rFlat, YUp(v), rc, Vector3.Zero);
                V(tip); V(bl); V(notch);
                V(tip); V(notch); V(br);
                heads++;
            }

            foreach (var raw in route.Legs)
            {
                if (raw.Count < 2) continue;
                var leg = new Vector3[raw.Count];
                for (int i = 0; i < raw.Count; i++) leg[i] = raw[i] + routeLift;
                pathPoints += leg.Length;

                // Arc length along the ground-following polyline. Arrowheads are placed by DISTANCE
                // WALKED, not by plan distance, so they stay evenly spaced up a slope.
                var cum = new float[leg.Length];
                for (int i = 1; i < leg.Length; i++) cum[i] = cum[i - 1] + Vector3.Distance(leg[i - 1], leg[i]);
                float total = cum[^1];
                if (total < 1e-3f) continue;

                for (int i = 0; i + 1 < leg.Length; i++)
                {
                    float segMid = MathF.Max(1f, Vector3.Distance((leg[i] + leg[i + 1]) * 0.5f, cam.Eye));
                    EmitEdge(leg[i], leg[i + 1], Vector3.UnitZ, rc, HalfWidthAt(segMid), rSolid, rDash);
                }

                // Position and local direction at a given distance along the leg.
                (Vector3 P, Vector3 D) At(float t)
                {
                    t = Math.Clamp(t, 0f, total);
                    int i = 0;
                    while (i + 2 < leg.Length && cum[i + 1] < t) i++;
                    var d = leg[i + 1] - leg[i];
                    float span = cum[i + 1] - cum[i];
                    float f = span > 1e-5f ? (t - cum[i]) / span : 0f;
                    var dir = d.LengthSquared() > 1e-10f ? Vector3.Normalize(d) : Vector3.UnitY;
                    return (leg[i] + d * f, dir);
                }

                // Arrowheads are laid out from the END of the leg BACKWARDS, so one always lands
                // pointing into the waypoint it leads to. That is the message — "this way to the
                // next one" — and it has to survive whatever the spacing arithmetic does to the
                // rest of the run. Spacing is set once per leg, from the leg's own midpoint
                // distance, so the walk terminates predictably; each head is then sized from where
                // it actually sits.
                float legMid = MathF.Max(1f, Vector3.Distance(At(total * 0.5f).P, cam.Eye));
                float widthMid = HalfWidthAt(legMid);
                float headMid = widthMid * HeadLengthInWidths;
                // Heads may never overlap however the two knobs are set: the floor is the one
                // place spacing is allowed to know the head's length.
                float every = route.ArrowEvery ?? MathF.Max(widthMid * ArrowEveryInWidths, headMid * 1.05f);
                float first = total - headMid * 0.55f;
                if (first <= headMid * 0.5f)
                { var (ap, ad) = At(total * 0.5f); Arrow(ap, ad); }       // too short to sit at the end without crowding the start
                else if (every <= 0f)
                { var (ap, ad) = At(first); Arrow(ap, ad); }              // --route-arrows end: exactly one, at the far end
                else
                {
                    every = MathF.Max(every, 8f);                         // a spacing under this is a runaway, not a request
                    int cap = 0;
                    for (float t = first; t > headMid * 0.5f && cap < 512; t -= every, cap++)
                    { var (ap, ad) = At(t); Arrow(ap, ad); }
                }
            }

            // A translucent fill at each AUTHORED waypoint — no rim. The disc says "this is a
            // vertex of the path" without drawing a second hard-edged shape into a picture that
            // already has the line, the arrows and a navmesh wire in it; translucent, so it marks
            // the ground rather than covering it.
            if (route.Discs)
            {
                const int DiscSegments = 48;
                const float DiscRadius = 48f;       // 0.75 x the 64 u the clearance probes reach
                foreach (var w in route.Waypoints)
                {
                    var c = w + routeLift;
                    for (int sg = 0; sg < DiscSegments; sg++)
                    {
                        float a0 = 2f * MathF.PI * sg / DiscSegments, a1 = 2f * MathF.PI * (sg + 1) / DiscSegments;
                        var e0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0f);
                        var e1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0f);
                        Overlay.Vertex(rDisc, YUp(c), rc, Vector3.Zero);
                        Overlay.Vertex(rDisc, YUp(c + e0 * DiscRadius), rc, Vector3.Zero);
                        Overlay.Vertex(rDisc, YUp(c + e1 * DiscRadius), rc, Vector3.Zero);
                    }
                }
            }

            // Discs first, so the line and its arrows draw ON the marker rather than under it.
            if (rDisc.Count > 0) layers.Add((rDisc.ToArray(), false, false, 3, 0.35f));
            if (rSolid.Count > 0) layers.Add((rSolid.ToArray(), true, false, 3, 1f));
            if (rDash.Count > 0) layers.Add((rDash.ToArray(), false, false, 3, 1f));
            if (rFlat.Count > 0) layers.Add((rFlat.ToArray(), false, false, 3, 1f));
            Console.WriteLine($"   route: {route.Waypoints.Count} waypoint(s), {route.Legs.Count} leg(s) drawn as "
                            + $"{pathPoints:N0} ground-following point(s), {heads} arrowhead(s)"
                            + (route.Discs ? $", {route.Waypoints.Count} translucent disc(s) at r{48}" : "")
                            + $"; runs {runVisible - v0:N0} visible, {runUnder - u0:N0} subterranean, "
                            + $"{runHidden - h0:N0} behind geometry (dashed copies cover the last two)");
        }

        // ---- the occlusion planes: the engine's culling volumes, drawn as what they are ----------
        // A PortalBox is its mid-quad (the slab is two units thick; drawing the slab would draw a
        // line), a Box is its six faces, fainter. Fill translucent and depth-tested, so a plane
        // inside a wall shows only where the wall does not cover it; the outline gets the nav wire's
        // treatment — a solid depth-tested copy and an always-visible dashed one — so a plane behind
        // a wall is drawn dashed where hidden and never lost. Layer tag 4: the fly-through's L key
        // hides the whole family at draw time, exactly as N hides the wire. Every plane in the list
        // is a candidate for the occlusion preview whether or not the layer is drawn; the pick
        // shapes below follow the layer, since a hover must not name what the picture does not show.
        if (planes.Count > 0)
        {
            // Built for the fly-through whether or not the layer opens shown — L is a visibility
            // flip, not a rebuild, exactly as N is for the wire. The static path builds it only
            // when asked, so a default render carries no plane geometry at all.
            if (showPlanes || interactive is not null)
            {
                var (quadFill, boxFill, pSolid, pDash) = PlaneOverlay.Build(planes, cam.Eye);
                if (quadFill.Count > 0) layers.Add((quadFill.ToArray(), true, false, 4, PlaneOverlay.QuadAlpha));
                if (boxFill.Count > 0) layers.Add((boxFill.ToArray(), true, false, 4, PlaneOverlay.BoxAlpha));
                if (pSolid.Count > 0) layers.Add((pSolid.ToArray(), true, false, 4, 1f));
                if (pDash.Count > 0) layers.Add((pDash.ToArray(), false, false, 4, 1f));
                int nPortal = planes.Count(p => p.Kind == PlaneKind.PortalBox);
                Console.WriteLine($"   planes layer: {planes.Count} {(showPlanes ? "drawn" : "resident, hidden until L")} — {nPortal} PortalBox as translucent mid-quads, "
                                + $"{planes.Count - nPortal} Box as fainter boxes; outline solid where visible, dashed where hidden; "
                                + "colour = provenance (grey vanilla, gold inherited, magenta added, teal moved)");
            }
            if (interactive is not null)
                foreach (var p in planes)
                {
                    var (pv, pt) = PlaneGeometry.PickTriangles(p);
                    var ps = new PickShape { Kind = PickKind.Plane, Plane = p, Verts = pv.Select(YUp).ToArray(), Tris = pt };
                    ps.ComputeBounds();
                    interactive.PickShapes.Add(ps);
                }
        }

        // The overlay pass itself: viewprobe's own flat shader, so an outline is the exact
        // palette colour rather than whatever the scene lighting does to it, and so the hidden
        // layers can drop the depth test.
        int wireProg = 0, wireVao = 0, wireVbo = 0;
        var ghostData = haloData;
        var ghostNearData = haloNear.Count > 0 ? haloNear.ToArray() : null;
        if (layers.Count > 0)
        {
            wireProg = OverlayShader();
            wireVao = GL.GenVertexArray(); wireVbo = GL.GenBuffer();
            GL.BindVertexArray(wireVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, wireVbo);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 9 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 9 * sizeof(float), 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, 9 * sizeof(float), 6 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.BindVertexArray(0);

            wireOverlay = (vmIn, pmIn) =>
            {
                GL.UseProgram(wireProg);
                var vm = vmIn; var pm = pmIn;
                GL.UniformMatrix4(GL.GetUniformLocation(wireProg, "u_view"), false, ref vm);
                GL.UniformMatrix4(GL.GetUniformLocation(wireProg, "u_projection"), false, ref pm);
                GL.BindVertexArray(wireVao);
                GL.BindBuffer(BufferTarget.ArrayBuffer, wireVbo);
                GL.Disable(EnableCap.Blend);
                GL.DepthMask(false);
                int alphaLoc = GL.GetUniformLocation(wireProg, "u_alpha");
                GL.Uniform1(alphaLoc, 1.0f);
                foreach (var (data, depthTested, cullBack, nav, layerAlpha) in layers)
                {
                    // Live navmesh display mode: the flags are read HERE, every frame, so the N
                    // key changes the next frame and nothing is rebuilt. Null (static path) means
                    // draw everything the build decided to make.
                    if (nav == 1 && interactive is { ShowNavWire: false }) continue;
                    if (nav == 2 && interactive is { ShowNavRedWire: false }) continue;
                    if (nav == 3 && interactive is { ShowRoute: false }) continue;
                    if (nav == 4 && interactive is { ShowPlanes: false }) continue;
                    // At alpha 1 this is what the loop always did; below it, ordinary source-alpha
                    // blending, which is the whole mechanism behind a translucent overlay layer.
                    if (layerAlpha < 1f)
                    {
                        GL.Enable(EnableCap.Blend);
                        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                        GL.Uniform1(alphaLoc, layerAlpha);
                    }
                    else { GL.Disable(EnableCap.Blend); GL.Uniform1(alphaLoc, 1.0f); }
                    if (depthTested) { GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Lequal); }
                    else GL.Disable(EnableCap.DepthTest);
                    if (cullBack) { GL.Enable(EnableCap.CullFace); GL.CullFace(CullFaceMode.Back); }
                    else GL.Disable(EnableCap.CullFace);
                    GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StreamDraw);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, data.Length / 9);
                }
                if (ghostData is not null)
                {
                    // The ghost: the hull's far faces again, drawn ONLY where the scene is
                    // closer (DepthFunc GREATER), blended translucent. A hidden highlight reads
                    // as an amber silhouette through whatever hides it; a visible one gets a
                    // subtle wash (its own front surface is closer than the hull's far face),
                    // so a recessed door whose rim hides behind the jamb still reads highlighted.
                    GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Greater);
                    GL.Enable(EnableCap.CullFace); GL.CullFace(CullFaceMode.Back);
                    GL.Enable(EnableCap.Blend);
                    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    // 0.22 per layer: the hull's far-side surfaces stack (back face + frame
                    // pieces ≈ 2 layers → ~0.4 combined), so the per-draw alpha must sit well
                    // under the intended read or the wash turns back into a repaint.
                    GL.Uniform1(alphaLoc, 0.22f);
                    GL.BufferData(BufferTarget.ArrayBuffer, ghostData.Length * sizeof(float), ghostData, BufferUsageHint.StreamDraw);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, ghostData.Length / 9);
                    if (ghostNearData is not null)
                    {
                        // Deep-occlusion boost: near faces pass GREATER only when the occluder
                        // is in front of the whole hull (a wall), never for the object's own
                        // surface or its jamb — so only the truly hidden case gets brighter.
                        GL.Uniform1(alphaLoc, 0.25f);
                        GL.BufferData(BufferTarget.ArrayBuffer, ghostNearData.Length * sizeof(float), ghostNearData, BufferUsageHint.StreamDraw);
                        GL.DrawArrays(PrimitiveType.Triangles, 0, ghostNearData.Length / 9);
                    }
                    GL.Uniform1(alphaLoc, 1.0f);
                    GL.Disable(EnableCap.Blend);
                    GL.DepthFunc(DepthFunction.Lequal);
                }
                GL.Enable(EnableCap.DepthTest);
                GL.Enable(EnableCap.CullFace);
                GL.DepthMask(true);
                GL.BindVertexArray(0);
            };
        }

        // ---- INTERACTIVE: hand the resident scene to the fly-through loop -----------------
        // Everything above — scene, overlay layers, pass-1 colour/classification — is shared
        // with the static path; only the final draw differs. The offscreen pass-1 target is
        // finished with (the loop draws to the window's own framebuffer), so it goes first.
        // THE BUILD CENSUS. A local function, and called on BOTH exits, because for most of this
        // renderer's life it sat after the PNG save and the fly-through returned before reaching
        // it — so an --interactive run silently printed no shape count, no cull-above tally, no
        // covering census and no unshadeable-shape line. The counts a person uses to decide
        // whether a picture can be trusted were missing from exactly the mode where they cannot
        // read them off a caption (found 2026-08-30, when the covering census went missing from a
        // fly-through and the absence was briefly mistaken for "no coverings were drawn").
        void PrintBuildCensus()
        {
        Console.WriteLine($"   textured: {shapes:N0} shapes ({texturedShapes:N0} with a real diffuse), {missingMesh:N0} refs without a resolvable mesh"
                            + $"; {sizeW}x{sizeH}, {(samples >= 2 ? samples + "x msaa" : "no msaa")}");
            if (cullAbove is { } cz2)
                Console.WriteLine($"   cull-above z {cz2:0}: {cullTrisDropped:N0} static triangle(s) removed, "
                                + $"{cullShapesDropped:N0} shape(s) wholly above the cut (terrain/nav/actors/overlay untouched)");
            if (fxStaticShapes > 0)
                Console.WriteLine($"   static effect shapes: {fxStaticShapes:N0} DRAWN on {fxStaticRefCount:N0} ref(s) "
                                + "— --fx-static, frozen at the authored frame, additive, no depth write");
            if (coveringShapes > 0)
            {
                Console.WriteLine($"   projected coverings (MATO): {coveringShapes:N0} shell(s) on {coveringRefs:N0} ref(s) "
                                + $"of {coveringByMato.Count} material(s) — STAT.Material + MaxAngle, --no-coverings to drop");
                foreach (var kv in coveringByMato.OrderByDescending(k => k.Value))
                    Console.WriteLine($"      {kv.Value,5} shell(s)  {kv.Key}");
            }
            if (fxShapesSkipped + nmShapesSkipped > 0)
            {
                Console.WriteLine($"   unshadeable shapes: {fxShapesSkipped + nmShapesSkipped:N0} skipped on {fxRefCount:N0} ref(s) "
                                + $"of {fxByMesh.Count:N0} mesh(es) — {fxShapesSkipped:N0} BSEffectShaderProperty, "
                                + $"{nmShapesSkipped:N0} normal-map-as-diffuse; --fx to draw them");
                if (listEffectMeshes)
                    foreach (var kv in fxByMesh.OrderByDescending(k => k.Value))
                        Console.WriteLine($"      {kv.Value,4} unshadeable shape(s)  {kv.Key}");
            }
            if (textures.MissingTexturePaths.Count > 0)
                Console.WriteLine($"   textures the resolver could NOT find (white fallback, counted per rule 6): {textures.MissingTexturePaths.Count:N0}");
        }

        if (interactive is not null)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(fbo); GL.DeleteTexture(colorTex); GL.DeleteRenderbuffer(depthRb);
            if (msFbo != 0) { GL.DeleteFramebuffer(msFbo); GL.DeleteRenderbuffer(msColorRb); GL.DeleteRenderbuffer(msDepthRb); }

            // BEFORE the window opens, not after it closes: the console is what a person reads
            // while deciding where to fly, and a census printed on exit is a census nobody used.
            PrintBuildCensus();

            InteractiveViewer.Run(interactive, renderer, cam, wireOverlay);

            if (wireProg != 0) { GL.DeleteProgram(wireProg); GL.DeleteVertexArray(wireVao); GL.DeleteBuffer(wireVbo); }
            foreach (var bm in borrowedNpcMeshes) renderer.RemoveMesh(bm);
            foreach (var m in meshes) m.Dispose();
            GL.DeleteTexture(orange);
            return shapes;
        }

        float[]? pickDepth = picks.Count > 0 || npcActors.Count > 0 || depthDumpPath is not null ? new float[sizeW * sizeH] : null;
        var pixels = Draw(pickDepth);
        // --depth-dump: the finished frame's depth buffer, exactly as --pick reads it below — size*size
        // float32 window depth in [0,1], GL row order (row 0 = the BOTTOM image row), 1.0 = background.
        // A distance along the view axis is the standard perspective inverse with the near/far printed
        // here; a distance along a pixel's own ray follows from that pixel's angles. Nothing else in the
        // output changes: the file is written beside whatever the caller asked for and named on the console.
        if (depthDumpPath is not null && pickDepth is not null)
        {
            var dumpDir = Path.GetDirectoryName(Path.GetFullPath(depthDumpPath));
            if (!string.IsNullOrEmpty(dumpDir)) Directory.CreateDirectory(dumpDir);
            File.WriteAllBytes(depthDumpPath, System.Runtime.InteropServices.MemoryMarshal.AsBytes(pickDepth.AsSpan()).ToArray());
            Console.WriteLine($"   depth-dump: {Path.GetFullPath(depthDumpPath)}  ({sizeW}x{sizeH} float32 window depth, rows bottom-up, "
                            + $"1.0 = background; near {NearPlane:0} far {FarPlane:0}; fov {cam.FovDeg:0.##} square)");
        }
        if (wireProg != 0) { GL.DeleteProgram(wireProg); GL.DeleteVertexArray(wireVao); GL.DeleteBuffer(wireVbo); }

        // --pick: "what is at this pixel". Reads the finished frame's depth and unprojects it,
        // which is the only way to turn something SEEN in a render back into a coordinate that
        // can be queried against the load order. Pixels are image coordinates — top-left origin,
        // the numbers an image viewer shows — so the buffer's bottom-left origin is flipped here
        // rather than in the caller's head.
        if (pickDepth is not null)
        {
            var invVp = viewProj.Inverted();
            foreach (var (ix, iy) in picks)
            {
                if (ix < 0 || ix >= sizeW || iy < 0 || iy >= sizeH)
                { Console.WriteLine($"   pick {ix},{iy}: outside a {sizeW}x{sizeH} frame"); continue; }
                int gy = sizeH - 1 - iy;
                float d = pickDepth[gy * sizeW + ix];
                float nx = (ix + 0.5f) / sizeW * 2f - 1f;
                float ny = (gy + 0.5f) / sizeH * 2f - 1f;
                // The planes are blended with depth writes off, so the depth buffer never holds
                // one: the pixel's ray is tested against the drawn planes on the CPU first, and a
                // plane it crosses is reported BEFORE what the depth buffer says is behind it.
                if (showPlanes && planes.Count > 0)
                {
                    var f4 = GlVec4.TransformRow(new GlVec4(nx, ny, 1f, 1f), invVp);
                    if (MathF.Abs(f4.W) > 1e-9f)
                    {
                        var fy = new Vector3(f4.X / f4.W, f4.Y / f4.W, f4.Z / f4.W);
                        var farS = new Vector3(fy.X, -fy.Z, fy.Y);
                        var dir = Unit(farS - cam.Eye);
                        var hits = planes.Select(p => (P: p, T: PlaneGeometry.RayHit(p, cam.Eye, dir, FarPlane)))
                                         .Where(h => h.T is not null).OrderBy(h => h.T).ToList();
                        foreach (var (p, t) in hits)
                            Console.WriteLine($"   pick {ix},{iy}: PLANE {p.Key} = FormID {p.RuntimeId} — {p.KindLabel} {p.ExtentLabel}, "
                                            + $"{p.ProvenanceDetail} — crossed {t!.Value:0} u from the eye; behind it:");
                    }
                }
                if (d >= 0.999999f)
                { Console.WriteLine($"   pick {ix},{iy}: background — no geometry under this pixel"); continue; }
                var w4 = GlVec4.TransformRow(new GlVec4(nx, ny, d * 2f - 1f, 1f), invVp);
                if (MathF.Abs(w4.W) < 1e-9f) { Console.WriteLine($"   pick {ix},{iy}: degenerate"); continue; }
                var wy = new Vector3(w4.X / w4.W, w4.Y / w4.W, w4.Z / w4.W);
                var ws = new Vector3(wy.X, -wy.Z, wy.Y);          // Y-up back to Skyrim's Z-up
                string land = terrainHeightAt is not null && terrainHeightAt(ws.X, ws.Y) is { } tz
                    ? $", LAND surface z {tz:0} ({(ws.Z - tz):+0;-0} vs the surface)"
                    : ", no LAND here";
                Console.WriteLine($"   pick {ix},{iy}: {ws.X:0},{ws.Y:0},{ws.Z:0}"
                                + $"  cell ({(int)MathF.Floor(ws.X / 4096f)},{(int)MathF.Floor(ws.Y / 4096f)})"
                                + $"  {Vector3.Distance(ws, cam.Eye):0} u from the eye{land}");
            }
        }

        // Is each actor actually IN the picture? The puck rim pierces everything by
        // design, so an actor under an upper floor renders as an EMPTY RING — which
        // reads as a defect unless the console says what happened and names the fix.
        // Same depth compare (same slack constants) as the navmesh dash classifier,
        // sampled at the actor's head; the head pixel is the actor's own surface when
        // visible, so only a genuinely nearer foreign surface trips the warning.
        if (pickDepth is not null && npcActors.Count > 0)
        {
            foreach (var actor in npcActors)
            {
                float aScale = actor.PuckRadius / 34f;                       // PuckRadius = 34 x world scale
                var headY = YUp(actor.PositionSkyrim + new Vector3(0, 0, 110f * aScale));
                if (ScreenOf(headY) is not { } hp)
                { Console.WriteLine($"   [npc] {actor.Label}: outside the frame at this camera"); continue; }
                float sceneD = ViewDist(pickDepth[hp.Y * sizeW + hp.X]);      // ScreenOf is GL bottom-left, like the buffer
                float mineD = Vector3.Dot(headY - eyeY, viewFwd);
                // The nav classifier's slack, PLUS the actor's own headroom: the sample
                // point sits inside the head, so the visible surface at that pixel is the
                // actor's own hood/hair a dozen units nearer — which must not read as
                // "hidden". Only a surface more than ~a head farther out than the actor's
                // own shell can be foreign (a floor, a roof), and that is the finding.
                if (mineD > sceneD * occlRel + occlAbs + 30f * aScale)
                    Console.WriteLine($"   [npc] {actor.Label}: HIDDEN behind scene geometry at this camera "
                                    + $"(nearest surface {sceneD:0} u out, actor {mineD:0} u) — only the puck rim shows; "
                                    + $"slice the view with --cull-above Z (e.g. {actor.PositionSkyrim.Z + 140f * aScale:0}) "
                                    + "or move the camera below the occluder");
            }
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DeleteFramebuffer(fbo); GL.DeleteTexture(colorTex); GL.DeleteRenderbuffer(depthRb);
        if (msFbo != 0) { GL.DeleteFramebuffer(msFbo); GL.DeleteRenderbuffer(msColorRb); GL.DeleteRenderbuffer(msDepthRb); }

        SaveFrame(pixels, sizeW, sizeH, capLines, colourCaptionLines, pngTextMeta, outPath,
                  gridSpacing > 0f ? gridLabels : null, gridColour3, reportMode);

        // Give back the borrowed NPC meshes BEFORE this renderer can dispose them —
        // ClearMeshes()/Dispose() destroy what they hold, and the actors' VAOs must
        // survive into the next compare panel. Their owners (the VMs) are disposed
        // by Program.cs after the last panel.
        foreach (var bm in borrowedNpcMeshes) renderer.RemoveMesh(bm);

        foreach (var m in meshes) m.Dispose();
        GL.DeleteTexture(orange);
        PrintBuildCensus();
        return shapes;
    }

    /// <summary>Flip, force opaque, burn the caption bar, embed the replication chunk, save.
    /// Shared by the static path and the interactive F12 capture — one encoding of "what a
    /// viewprobe PNG is". Caption metrics are in units of the 1280-wide frame they were tuned
    /// on, scaled by WIDTH (the static path is square, so this is the same scale it always
    /// used).</summary>
    /// <param name="pinnedLabels">Text burned at GIVEN IMAGE PIXELS (top-left origin) rather than in
    /// the caption bar — the grid's coordinate labels, which only mean anything beside the line they
    /// name. Null for a frame that has none.</param>
    internal static void SaveFrame(byte[] pixels, int width, int height,
        IReadOnlyList<string> captionLines, IReadOnlyList<(string Text, Vector3 Color)> colourCaptionLines,
        string? pngTextMeta, string outPath,
        IReadOnlyList<(string Text, int X, int Y)>? pinnedLabels = null, Vector3 pinnedColor = default,
        bool reportMode = false)
    {
        if (reportMode)
        {
            captionLines = Array.Empty<string>();
            colourCaptionLines = Array.Empty<(string, Vector3)>();
            pinnedLabels = null;
        }
        // The frame is a screenshot: opaque by definition. The colour buffer's ALPHA, though, is
        // whatever the material shader happened to write — the ground comes back at alpha ~50 and
        // alpha-blended canopy at 0 — and ImageSharp honours it, so the compare stitch was
        // compositing each panel over its black canvas and darkening most of the picture (the
        // panel PNGs were fine; the stitched file everyone actually looks at was not). Force it.
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        using var img = Image.LoadPixelData<Rgba32>(pixels, width, height);
        img.Mutate(x => x.Flip(FlipMode.Vertical));
        // Burn the caption into the frame so it travels with the PNG into any viewer. The font and
        // the bar's height are resolved ONCE, out here, because the pinned labels below have to
        // stay clear of the bar — a coordinate label hidden under the caption is a label that is
        // not there.
        float capScale = width / 1280f;
        Font? font = null;
        foreach (var famName in new[] { "Segoe UI", "Arial", "Verdana" })
            if (SystemFonts.TryGet(famName, out var fam)) { font = fam.CreateFont(15f * capScale, FontStyle.Bold); break; }
        if (font is null)
            foreach (var fam in SystemFonts.Families) { font = fam.CreateFont(15f * capScale, FontStyle.Bold); break; }
        int capLineH = (int)(22f * capScale);
        int capBarH = captionLines.Count + colourCaptionLines.Count > 0
                    ? (int)(10f * capScale) + capLineH * (captionLines.Count + colourCaptionLines.Count) : 0;
        if (captionLines.Count + colourCaptionLines.Count > 0)
        {
            if (font is not null)
            {
                int lineH = capLineH, barH = capBarH;
                var f2 = font;
                img.Mutate(x =>
                {
                    x.Fill(SixLabors.ImageSharp.Color.FromRgba(8, 10, 14, 200), new SixLabors.ImageSharp.RectangleF(0, 0, width, barH));
                    for (int i = 0; i < captionLines.Count; i++)
                        x.DrawText(captionLines[i], f2, SixLabors.ImageSharp.Color.White,
                                   new SixLabors.ImageSharp.PointF(12f * capScale, 7f * capScale + lineH * i));
                    // Each actor's caption line wears that actor's identity colour — the
                    // same one its puck fill and rim wear — so "who is who" is answered by
                    // the caption itself, not by counting rings. The route's line rides the same
                    // channel in the route colour, for the same reason.
                    for (int j = 0; j < colourCaptionLines.Count; j++)
                    {
                        var (txt, c) = colourCaptionLines[j];
                        x.DrawText(txt, f2,
                                   SixLabors.ImageSharp.Color.FromRgb(
                                       (byte)Math.Clamp(c.X * 255f, 0f, 255f),
                                       (byte)Math.Clamp(c.Y * 255f, 0f, 255f),
                                       (byte)Math.Clamp(c.Z * 255f, 0f, 255f)),
                                   new SixLabors.ImageSharp.PointF(12f * capScale, 7f * capScale + lineH * (captionLines.Count + j)));
                    }
                });
            }
            else
                Console.WriteLine("   caption: no system font found; image saved without caption");
        }

        // ---- pinned labels: the grid's coordinates, written where its lines meet the frame -----
        // Smaller than the caption and drawn TWICE — a near-black copy one pixel down-right, then
        // the coloured one — because these land on whatever the render happens to put under them
        // (pale stone, dark shadow, a lit floor), and a caption bar is not an option for text that
        // has to sit beside the line it names. The shadow is what makes one glyph set legible on
        // every background instead of on most of them.
        if (pinnedLabels is { Count: > 0 } && font is not null)
        {
            var lf = font.Family.CreateFont(12f * capScale, FontStyle.Bold);
            var ink = SixLabors.ImageSharp.Color.FromRgb(
                (byte)Math.Clamp(pinnedColor.X * 255f, 0f, 255f),
                (byte)Math.Clamp(pinnedColor.Y * 255f, 0f, 255f),
                (byte)Math.Clamp(pinnedColor.Z * 255f, 0f, 255f));
            var shadow = SixLabors.ImageSharp.Color.FromRgba(0, 0, 0, 200);
            img.Mutate(x =>
            {
                foreach (var (text, lx, ly) in pinnedLabels)
                {
                    // Advance width is ESTIMATED, and only ever to keep a label inside the frame —
                    // a label clipped in half is unreadable, a label two pixels off centre is not.
                    float est = text.Length * 12f * capScale * 0.62f;
                    float px = Math.Clamp(lx, 2f, MathF.Max(2f, width - est - 2f));
                    float py = Math.Clamp(ly, capBarH + 2f, MathF.Max(capBarH + 2f, height - 14f * capScale));
                    x.DrawText(text, lf, shadow, new SixLabors.ImageSharp.PointF(px + 1f, py + 1f));
                    x.DrawText(text, lf, ink, new SixLabors.ImageSharp.PointF(px, py));
                }
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        // Replication chunk: the camera pose (and the arguments that compose it) rides
        // inside the file as a PNG tEXt entry keyed "viewprobe", so a shot can be re-run
        // from the image alone even when it is cropped away from its console transcript.
        if (pngTextMeta is { Length: > 0 })
            img.Metadata.GetPngMetadata().TextData.Add(
                new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData("viewprobe", pngTextMeta, string.Empty, string.Empty));
        img.SaveAsPng(outPath);
    }

    /// <summary>Overlay geometry that is not a navmesh edge: the dashed cell outline and the
    /// diff spheres. Vertices are 9 floats — position, colour, normal — where a ZERO normal means
    /// "flat, exactly this colour" and a real one means "shade me".</summary>
    internal static class Overlay
    {
        public static void Vertex(List<float> dst, Vector3 p, Vector3 colour, Vector3 normal)
        {
            dst.Add(p.X); dst.Add(p.Y); dst.Add(p.Z);
            dst.Add(colour.X); dst.Add(colour.Y); dst.Add(colour.Z);
            dst.Add(normal.X); dst.Add(normal.Y); dst.Add(normal.Z);
        }

        /// <summary>An unbroken ribbon along a Skyrim-space polyline, camera-facing like the dashed
        /// one, with its half-width taken from a caller-supplied law of DISTANCE — so one line keeps
        /// a constant width on screen along its whole length instead of tapering into the distance.
        /// Solid rather than dashed because this draws the grid, and a dashed ruler reads as a
        /// finding: every other dashed thing in this overlay means "hidden, but here".</summary>
        public static void SolidPolyline(List<float> dst, IReadOnlyList<Vector3> pts, Vector3 colour,
                                         Func<float, float> halfWidthAt, Vector3 eye)
        {
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var p = pts[i]; var q = pts[i + 1];
                var seg = q - p;
                float len = seg.Length();
                if (len < 1e-4f) continue;
                var dir = seg / len;
                var mid = (p + q) * 0.5f;
                var toEye = Vector3.Normalize(eye - mid);
                var side0 = Vector3.Cross(toEye, dir);
                if (side0.LengthSquared() < 1e-8f) side0 = Vector3.UnitZ;
                var side = YUp(Vector3.Normalize(side0) * halfWidthAt(MathF.Max(1f, Vector3.Distance(mid, eye))));
                var pa = YUp(p); var pb = YUp(q);
                Vertex(dst, pa - side, colour, Vector3.Zero);
                Vertex(dst, pa + side, colour, Vector3.Zero);
                Vertex(dst, pb + side, colour, Vector3.Zero);
                Vertex(dst, pa - side, colour, Vector3.Zero);
                Vertex(dst, pb + side, colour, Vector3.Zero);
                Vertex(dst, pb - side, colour, Vector3.Zero);
            }
        }

        /// <summary>A dashed ribbon along a Skyrim-space polyline, turned to face the camera so it
        /// keeps its width whatever the ground underneath is doing. Dash length tracks distance,
        /// so the dashes read the same size near and far.</summary>
        public static void DashedPolyline(List<float> dst, IReadOnlyList<Vector3> pts, Vector3 colour,
                                          float halfWidth, Vector3 eye)
        {
            float travelled = 0f;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var p = pts[i]; var q = pts[i + 1];
                var seg = q - p;
                float len = seg.Length();
                if (len < 1e-4f) continue;
                var dir = seg / len;
                var mid = (p + q) * 0.5f;
                float camLen = MathF.Max(1f, Vector3.Distance(mid, eye));
                float on = Math.Clamp(camLen * 0.012f, 3f, 400f);
                float period = on * 1.9f;
                // Camera-facing: cross(view direction, segment direction) is the on-screen normal.
                var toEye = Vector3.Normalize(eye - mid);
                var side0 = Vector3.Cross(toEye, dir);
                if (side0.LengthSquared() < 1e-8f) side0 = Vector3.UnitZ;
                var side = YUp(Vector3.Normalize(side0) * halfWidth);
                // Phase off the polyline start so consecutive segments keep one dash rhythm.
                for (float t = -(travelled % period); t < len; t += period)
                {
                    float a = MathF.Max(t, 0f), b = MathF.Min(t + on, len);
                    if (b <= a) continue;
                    var pa = YUp(p + dir * a); var pb = YUp(p + dir * b);
                    Vertex(dst, pa - side, colour, Vector3.Zero);
                    Vertex(dst, pa + side, colour, Vector3.Zero);
                    Vertex(dst, pb + side, colour, Vector3.Zero);
                    Vertex(dst, pa - side, colour, Vector3.Zero);
                    Vertex(dst, pb + side, colour, Vector3.Zero);
                    Vertex(dst, pb - side, colour, Vector3.Zero);
                }
                travelled += len;
            }
        }

        /// <summary>A small UV sphere at a Y-up centre. 10x6 is plenty: at these sizes the
        /// silhouette is what reads, and the shading is what makes it a ball and not a disc.</summary>
        public static void Sphere(List<float> dst, Vector3 centre, float radius, Vector3 colour)
        {
            const int Meridians = 10, Parallels = 6;
            static Vector3 P(int i, int j)
            {
                float phi = MathF.PI * j / Parallels;                 // 0..pi, pole to pole
                float theta = 2f * MathF.PI * i / Meridians;
                return new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
            }
            for (int j = 0; j < Parallels; j++)
                for (int i = 0; i < Meridians; i++)
                {
                    var n00 = P(i, j); var n10 = P(i + 1, j); var n11 = P(i + 1, j + 1); var n01 = P(i, j + 1);
                    void Emit(Vector3 n) => Vertex(dst, centre + n * radius, colour, n);
                    if (j > 0) { Emit(n00); Emit(n10); Emit(n11); }
                    if (j < Parallels - 1) { Emit(n00); Emit(n11); Emit(n01); }
                }
        }
    }

    /// <summary>The wire overlay's own program: position + colour straight through, no lighting.
    /// A diagnostic outline should be the colour it says it is, and the always-visible layers need
    /// a draw the library's mesh path cannot express (depth test off, per layer).</summary>
    static int OverlayShader()
    {
        const string vs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aCol;
layout(location=2) in vec3 aNrm;
uniform mat4 u_view;
uniform mat4 u_projection;
out vec3 vCol;
out vec3 vNrm;
void main(){ vCol = aCol; vNrm = aNrm; gl_Position = u_projection * u_view * vec4(aPos, 1.0); }";

        // x1.5, clamped: the palette entries are the values the auto-colour scorer reasons about,
        // and the library's flat-normal lighting used to land them on screen about half a stop
        // brighter. Keeping that keeps a cyan outline the same cyan it has always been.
        const string fs = @"#version 330 core
in vec3 vCol;
in vec3 vNrm;
uniform float u_alpha;   // 1.0 for every classic layer; <1 only for the highlight ghost pass
out vec4 frag;
void main(){
    vec3 c = min(vCol * 1.5, vec3(1.0));
    // A zero normal means flat: every line and ribbon takes this path, so their pixels are
    // bit-for-bit what they were before shading existed. Only the diff spheres carry a normal.
    if (dot(vNrm, vNrm) > 0.0001) {
        vec3 n = normalize(vNrm);
        c *= 0.45 + 0.55 * max(dot(n, normalize(vec3(0.40, 0.80, 0.45))), 0.0);
    }
    frag = vec4(c, u_alpha);
}";

        int v = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(v, vs); GL.CompileShader(v); Check(v, "vertex");
        int f = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(f, fs); GL.CompileShader(f); Check(f, "fragment");
        int p = GL.CreateProgram(); GL.AttachShader(p, v); GL.AttachShader(p, f); GL.LinkProgram(p);
        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("overlay shader link failed: " + GL.GetProgramInfoLog(p));
        GL.DeleteShader(v); GL.DeleteShader(f);
        return p;

        static void Check(int s, string what)
        {
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"overlay {what} shader: {GL.GetShaderInfoLog(s)}");
        }
    }

    static float[] Interleave(Vector3[] p, Vector3[] n, Vector2[] uv, Vector3[] t, Vector3[] b, Vector4[]? vc)
    {
        var data = new float[p.Length * 18];
        for (int i = 0; i < p.Length; i++)
        {
            int o = i * 18;
            data[o] = p[i].X; data[o + 1] = p[i].Y; data[o + 2] = p[i].Z;
            var nn = i < n.Length ? n[i] : Vector3.UnitY;
            data[o + 3] = nn.X; data[o + 4] = nn.Y; data[o + 5] = nn.Z;
            var u = i < uv.Length ? uv[i] : Vector2.Zero;
            data[o + 6] = u.X; data[o + 7] = u.Y;
            if (vc is not null && i < vc.Length)
            { data[o + 8] = vc[i].X; data[o + 9] = vc[i].Y; data[o + 10] = vc[i].Z; data[o + 11] = vc[i].W; }
            else
            { data[o + 8] = 1f; data[o + 9] = 1f; data[o + 10] = 1f; data[o + 11] = 1f; }
            var tt = i < t.Length ? t[i] : Vector3.Zero;
            data[o + 12] = tt.X; data[o + 13] = tt.Y; data[o + 14] = tt.Z;
            var bb = i < b.Length ? b[i] : Vector3.Zero;
            data[o + 15] = bb.X; data[o + 16] = bb.Y; data[o + 17] = bb.Z;
        }
        return data;
    }

    readonly record struct NifRootTransform(
        float R00, float R01, float R02,
        float R10, float R11, float R12,
        float R20, float R21, float R22,
        float Tx, float Ty, float Tz, float Scale)
    {
        public Vector3 UndoPoint(Vector3 p)
        {
            float x = p.X - Tx, y = p.Y - Ty, z = p.Z - Tz;
            float invScale = MathF.Abs(Scale) > 1e-8f ? 1f / Scale : 1f;
            return new Vector3(
                (R00 * x + R10 * y + R20 * z) * invScale,
                (R01 * x + R11 * y + R21 * z) * invScale,
                (R02 * x + R12 * y + R22 * z) * invScale);
        }

        public Vector3 UndoDirection(Vector3 d) => new(
            R00 * d.X + R10 * d.Y + R20 * d.Z,
            R01 * d.X + R11 * d.Y + R21 * d.Z,
            R02 * d.X + R12 * d.Y + R22 * d.Z);
    }

    // NifMeshBuilder is shared with an NPC portrait renderer, where composing every node through
    // the root is appropriate. Skyrim's placed-object path starts the model transform below the
    // root instead. The decisive game check is SHouseIntDoor02: root=-90 degrees, REFR=270 degrees;
    // composing both makes the door edge-on, while Skyrim draws it flush in its north-facing arch.
    static void StripNifRootTransform(string nifPath, IReadOnlyList<NifMeshBuilder.BuiltMesh> built)
    {
        using var nif = new nifly.NifFile();
        if (nif.Load(nifPath) != 0) return;
        var root = nif.GetRootNode();
        if (root is null) return;
        var t = root.GetTransformToParent();

        using var ex = new nifly.Vector3(); ex.x = 1f;
        using var ey = new nifly.Vector3(); ey.y = 1f;
        using var ez = new nifly.Vector3(); ez.z = 1f;
        using var c0 = t.rotation.opMult(ex);
        using var c1 = t.rotation.opMult(ey);
        using var c2 = t.rotation.opMult(ez);
        var inv = new NifRootTransform(
            c0.x, c1.x, c2.x,
            c0.y, c1.y, c2.y,
            c0.z, c1.z, c2.z,
            t.translation.x, t.translation.y, t.translation.z, t.scale);

        static Vector3 ToNif(Vector3 yup) => new(yup.X, -yup.Z, yup.Y);
        static Vector3 ToYUp(Vector3 nifSpace) => new(nifSpace.X, nifSpace.Z, -nifSpace.Y);
        static Vector3 UnitOrZero(Vector3 d) => d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.Zero;

        foreach (var b in built)
        {
            for (int i = 0; i < b.Positions.Length; i++)
                b.Positions[i] = ToYUp(inv.UndoPoint(ToNif(b.Positions[i])));
            for (int i = 0; i < b.Normals.Length; i++)
                b.Normals[i] = UnitOrZero(ToYUp(inv.UndoDirection(ToNif(b.Normals[i]))));
            for (int i = 0; i < b.Tangents.Length; i++)
                b.Tangents[i] = UnitOrZero(ToYUp(inv.UndoDirection(ToNif(b.Tangents[i]))));
            for (int i = 0; i < b.Bitangents.Length; i++)
                b.Bitangents[i] = UnitOrZero(ToYUp(inv.UndoDirection(ToNif(b.Bitangents[i]))));
        }
    }

}
