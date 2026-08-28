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
//                          chain (GetTransformToGlobal) and emits Y-up geometry.
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

/// <summary>One placed instance handed over from Program.cs: the mesh to draw and the Skyrim-space
/// placement to draw it at.</summary>
readonly record struct SceneRef(string MeshRelPath, Vec3 Position, Vec3 Rotation, float Scale, bool Highlight,
                                bool Marker = false);

static class TexturedScene
{
    // Z-up (Skyrim) -> Y-up (viewer): the exact swap NifMeshBuilder uses.
    static Vector3 YUp(Vector3 z) => new(z.X, z.Z, -z.Y);

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
        (float X0, float Y0, float X1, float Y1)? cellBounds, // target cell footprint; null = no outline
        IReadOnlyList<Vector3> diffVerts,                  // navmesh vertices unique to THIS panel
        Vector4 diffColor,                                 // colour for those vertices' spheres
        IReadOnlyList<(int X, int Y)> picks,                // image pixels to report world coords for
        IReadOnlyList<string> captionLines,                // burned into the image, top-left
        bool keepFx,                                       // --fx: draw effect-shader shapes too
        bool fxStatic,                                     // --fx-static: draw them as a frozen frame
        bool listEffectMeshes,                             // --list-meshes: name the meshes they came from
        int size, string outPath)
    {
        var logger = new ConsoleCvLogger();
        var gate = new CharacterViewerLogGate { Verbose = Environment.GetEnvironmentVariable("VP_CV_VERBOSE") == "1" };

        // MO2-style precedence for loose files: the resolver iterates LAST-to-first, so hand it
        // lowest-priority-first and put overwrite at the very end (it outranks every mod).
        var folders = new List<string>();
        for (int i = modFoldersHighestFirst.Count - 1; i >= 0; i--) folders.Add(modFoldersHighestFirst[i]);
        if (!string.IsNullOrWhiteSpace(overwriteDir) && Directory.Exists(overwriteDir)) folders.Add(overwriteDir);

        var resolver = new GameAssetResolver(new HcDataFolder(dataDir), new HcBsaProvider(assets), gate, logger);
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
        int shapes = 0, missingMesh = 0, texturedShapes = 0;
        // Effect-shader shapes dropped this render: the total shape-draws, and how many such
        // shapes each distinct mesh carries (a per-file property, so it is SET, not accumulated,
        // however many refs place that mesh).
        int fxShapesSkipped = 0, nmShapesSkipped = 0, fxRefCount = 0;
        int fxStaticShapes = 0, fxStaticRefCount = 0;
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
        // Near faces (outward winding), for the deep-occlusion boost: they pass a GREATER
        // depth test only when the occluder sits in front of the WHOLE hull — a wall, never
        // the object itself or its own door-jamb — so the through-wall ghost gets a second
        // blend layer and pops, while a visible object keeps only the light wash.
        var haloNear = new List<float>();
        var haloOrange = new Vector3(255f / 255f, 90f / 255f, 26f / 255f);

        foreach (var r in refs)
        {
            var rel = r.MeshRelPath.StartsWith("meshes", StringComparison.OrdinalIgnoreCase)
                ? r.MeshRelPath : Path.Combine("meshes", r.MeshRelPath);

            if (!builtCache.TryGetValue(rel, out var built))
            {
                built = null;
                var abs = resolver.ResolveAssetPath(rel);
                if (abs is not null)
                {
                    try { built = builder.BuildFromFile(abs); }
                    catch (Exception ex) { logger.LogError($"BuildFromFile('{rel}')", ex); }
                }
                builtCache[rel] = built;
            }
            if (built is null || built.Count == 0) { missingMesh++; continue; }

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

            // Skyrim-space placement. Skyrim's REFR angles are CLOCKWISE-positive (compass
            // headings; getangle z: 0=north, 90=east) while Matrix4x4.CreateRotation* is
            // right-handed counterclockwise-positive — so the angles must be NEGATED before
            // composing, or every rotated ref draws with its heading mirrored about north
            // (found 2026-08-28: the Falkreath walkway crossed its own navmesh in an X).
            var rot = SpatialOverlap.Euler(new Vec3(-r.Rotation.X, -r.Rotation.Y, -r.Rotation.Z),
                                           SpatialOverlap.EulerOrder.Xyz);
            var pos = new Vector3(r.Position.X, r.Position.Y, r.Position.Z);

            bool refHadFx = false, refHadFxStatic = false;
            foreach (var b in built)
            {
                int n = b.Positions.Length;
                if (n == 0 || b.Indices.Length == 0) continue;

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
                    if (b.HasEffectShader) fxShapesSkipped++; else nmShapesSkipped++;
                    refHadFx = true;
                    fxByMesh[rel] = fxByMesh.TryGetValue(rel, out var already)
                        ? already
                        : built.Count(x => x.HasEffectShader
                                           || (x.TexturePaths.TryGetValue(0, out var s0)
                                               && s0.EndsWith("_n.dds", StringComparison.OrdinalIgnoreCase)));
                    continue;
                }
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

                var mesh = new GlMesh();
                mesh.Upload(Interleave(wp, wn, b.TextureCoordinates, wt, wb, b.VertexColors), b.Indices);
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

                if (r.Highlight)
                {
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
                    for (int i = 0; i + 2 < b.Indices.Length; i += 3)
                    {
                        var p0 = sp[b.Indices[i]]; var p1 = sp[b.Indices[i + 1]]; var p2 = sp[b.Indices[i + 2]];
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
                            Overlay.Vertex(haloTris, p2, haloOrange, Vector3.Zero);
                            Overlay.Vertex(haloTris, p1, haloOrange, Vector3.Zero);
                            Overlay.Vertex(haloTris, p0, haloOrange, Vector3.Zero);
                            Overlay.Vertex(haloNear, p0, haloOrange, Vector3.Zero);
                            Overlay.Vertex(haloNear, p1, haloOrange, Vector3.Zero);
                            Overlay.Vertex(haloNear, p2, haloOrange, Vector3.Zero);
                        }
                        else
                        {
                            Overlay.Vertex(haloTris, p0, haloOrange, Vector3.Zero);
                            Overlay.Vertex(haloTris, p1, haloOrange, Vector3.Zero);
                            Overlay.Vertex(haloTris, p2, haloOrange, Vector3.Zero);
                            Overlay.Vertex(haloNear, p2, haloOrange, Vector3.Zero);
                            Overlay.Vertex(haloNear, p1, haloOrange, Vector3.Zero);
                            Overlay.Vertex(haloNear, p0, haloOrange, Vector3.Zero);
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
            }
            if (refHadFx) fxRefCount++;
            if (refHadFxStatic) fxStaticRefCount++;
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

        // First-person camera: eye/look given in Skyrim coordinates, expressed on the OrbitCamera.
        const float NearPlane = 4f, FarPlane = 131072f;
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
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, size, size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colorTex, 0);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRb);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, size, size);
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
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, samples, RenderbufferStorage.Rgba8, size, size);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, msColorRb);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msDepthRb);
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, samples, RenderbufferStorage.DepthComponent24, size, size);
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

        Action? wireOverlay = null;              // viewprobe's own wire pass, installed once built

        byte[] Draw(float[]? depthOut = null)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, drawFbo);
            GL.Viewport(0, 0, size, size);
            GL.ClearColor(skyColor.X, skyColor.Y, skyColor.Z, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.Render(camera, size, size);   // clears again, with renderer.ClearColor = sky
            wireOverlay?.Invoke();
            if (msFbo != 0)
            {
                // Resolve colour AND depth: the occlusion test reads the depth buffer back, so it
                // has to come from the same resolved frame the pixels did.
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, msFbo);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
                GL.BlitFramebuffer(0, 0, size, size, 0, 0, size, size,
                                   ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit,
                                   BlitFramebufferFilter.Nearest);
            }
            GL.Finish();
            var px = new byte[size * size * 4];
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.UnsignedByte, px);
            if (depthOut is not null)
                GL.ReadPixels(0, 0, size, size, PixelFormat.DepthComponent, PixelType.Float, depthOut);
            return px;
        }

        bool wantNav = (navTris.Count > 0 || redTris.Count > 0) && navMode != "off";
        bool wireMode = navMode is "wire" or "both";
        bool fillMode = navMode is "filled" or "both";
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
        var projMat = camera.GetProjectionMatrix(1f);
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
            return (Math.Clamp((int)((nx * 0.5f + 0.5f) * size), 0, size - 1),
                    Math.Clamp((int)((ny * 0.5f + 0.5f) * size), 0, size - 1));
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
            if (wantDepth) sceneDepth = new float[size * size];
            var bg = Draw(sceneDepth);
            if (autoColour && navTris.Count > 0)
            {
                float sr = 0, sg = 0, sb = 0; int hits = 0;
                int stride = Math.Max(1, navTris.Count / 600);
                for (int i = 0; i < navTris.Count; i += stride)
                {
                    var (a, b, c) = navTris[i];
                    if (ScreenOf(YUp((a + b + c) / 3f + lift)) is not { } sp) continue;
                    int o = (sp.Y * size + sp.X) * 4;
                    sr += bg[o] / 255f; sg += bg[o + 1] / 255f; sb += bg[o + 2] / 255f; hits++;
                }
                if (hits > 0)
                {
                    var mean = new Vector3(sr / hits, sg / hits, sb / hits);
                    float best = float.MinValue; string chosen = "green";
                    foreach (var (name, colr) in palette)
                    {
                        var c3 = new Vector3(colr.X, colr.Y, colr.Z);
                        // Contrast with what is under the mesh, plus distance from the reserved red.
                        float score = (c3 - mean).Length() + 0.5f * (c3 - new Vector3(red.X, red.Y, red.Z)).Length();
                        if (score > best) { best = score; chosen = name; healthy = colr; }
                    }
                    Console.WriteLine($"   nav colour: auto -> {chosen} (sampled {hits} points under the mesh, "
                                    + $"background {(int)(mean.X * 255)},{(int)(mean.Y * 255)},{(int)(mean.Z * 255)})");
                }
                else
                    Console.WriteLine("   nav colour: auto -> green (mesh not on screen; nothing to sample)");
            }
            else if (autoColour)
                Console.WriteLine("   nav colour: auto -> green (no healthy navmesh to sample under)");
        }

        // The translucent sheet still goes through the library renderer — it is surface, not
        // annotation, and it is supposed to be hidden by whatever covers it.
        if (wantNav && fillMode)
        {
            float alpha = navMode == "both" ? 0.16f : 0.35f;
            if (navTris.Count > 0)
            {
                var m0 = BuildFill(navTris, new Vector4(healthy.X, healthy.Y, healthy.Z, alpha), "navmesh-fill");
                m0.IsDecal = true;             // same insertion-ordered channel, added after the ground
                renderer.AddMesh(m0); meshes.Add(m0);
            }
            if (redTris.Count > 0)
            {
                var m1 = BuildFill(redTris, new Vector4(0.95f, 0.15f, 0.08f, MathF.Min(0.55f, alpha + 0.2f)), "navmesh-fill-red");
                m1.IsDecal = true;
                renderer.AddMesh(m1); meshes.Add(m1);
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
            float scene = ViewDist(sceneDepth[sp.Y * size + sp.X]);
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
        var layers = new List<(float[] Data, bool DepthTested, bool CullBack)>();
        // Highlight halos: the SAME hull buffer is drawn twice — solid depth-tested rim here,
        // then again as the translucent Greater-pass ghost (see the draw loop).
        float[]? haloData = haloTris.Count > 0 ? haloTris.ToArray() : null;
        if (haloData is not null) layers.Add((haloData, true, true));
        if (wantNav)
        {
            if (wireMode && navTris.Count > 0)
            {
                var (s, d) = BuildWire(navTris, healthy, 2.2f);
                if (s.Count > 0) layers.Add((s.ToArray(), true, false));
                if (d.Count > 0) layers.Add((d.ToArray(), false, false));
            }
            // The finding is ALWAYS outlined, wider than the healthy wire, in every mode that
            // draws navmesh at all.
            if (redTris.Count > 0)
            {
                var (s, d) = BuildWire(redTris, red, 3.4f);
                if (s.Count > 0) layers.Add((s.ToArray(), true, false));
                if (d.Count > 0) layers.Add((d.ToArray(), false, false));
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
                layers.Add((outline.ToArray(), false, false));
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
            layers.Add((spheres.ToArray(), false, true));
            Console.WriteLine($"   nav diff: {diffVerts.Count} vertex sphere(s) — vertices this version has and the other does not");
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

            wireOverlay = () =>
            {
                GL.UseProgram(wireProg);
                var vm = viewMat; var pm = projMat;
                GL.UniformMatrix4(GL.GetUniformLocation(wireProg, "u_view"), false, ref vm);
                GL.UniformMatrix4(GL.GetUniformLocation(wireProg, "u_projection"), false, ref pm);
                GL.BindVertexArray(wireVao);
                GL.BindBuffer(BufferTarget.ArrayBuffer, wireVbo);
                GL.Disable(EnableCap.Blend);
                GL.DepthMask(false);
                int alphaLoc = GL.GetUniformLocation(wireProg, "u_alpha");
                GL.Uniform1(alphaLoc, 1.0f);
                foreach (var (data, depthTested, cullBack) in layers)
                {
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

        float[]? pickDepth = picks.Count > 0 ? new float[size * size] : null;
        var pixels = Draw(pickDepth);
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
                if (ix < 0 || ix >= size || iy < 0 || iy >= size)
                { Console.WriteLine($"   pick {ix},{iy}: outside a {size}x{size} frame"); continue; }
                int gy = size - 1 - iy;
                float d = pickDepth[gy * size + ix];
                if (d >= 0.999999f)
                { Console.WriteLine($"   pick {ix},{iy}: background — no geometry under this pixel"); continue; }
                float nx = (ix + 0.5f) / size * 2f - 1f;
                float ny = (gy + 0.5f) / size * 2f - 1f;
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

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DeleteFramebuffer(fbo); GL.DeleteTexture(colorTex); GL.DeleteRenderbuffer(depthRb);
        if (msFbo != 0) { GL.DeleteFramebuffer(msFbo); GL.DeleteRenderbuffer(msColorRb); GL.DeleteRenderbuffer(msDepthRb); }

        // The frame is a screenshot: opaque by definition. The colour buffer's ALPHA, though, is
        // whatever the material shader happened to write — the ground comes back at alpha ~50 and
        // alpha-blended canopy at 0 — and ImageSharp honours it, so the compare stitch was
        // compositing each panel over its black canvas and darkening most of the picture (the
        // panel PNGs were fine; the stitched file everyone actually looks at was not). Force it.
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        using (var img = Image.LoadPixelData<Rgba32>(pixels, size, size))
        {
            img.Mutate(x => x.Flip(FlipMode.Vertical));
            if (captionLines.Count > 0)
            {
                // Burn the caption into the frame so it travels with the PNG into any viewer.
                // Everything about the caption is in units of the 1280-wide frame it was
                // tuned on, so --size 2560 gets text twice as tall and the bar reads the same.
                float capScale = size / 1280f;
                Font? font = null;
                foreach (var famName in new[] { "Segoe UI", "Arial", "Verdana" })
                    if (SystemFonts.TryGet(famName, out var fam)) { font = fam.CreateFont(15f * capScale, FontStyle.Bold); break; }
                if (font is null)
                    foreach (var fam in SystemFonts.Families) { font = fam.CreateFont(15f * capScale, FontStyle.Bold); break; }
                if (font is not null)
                {
                    int lineH = (int)(22f * capScale), barH = (int)(10f * capScale) + lineH * captionLines.Count;
                    var f2 = font;
                    img.Mutate(x =>
                    {
                        x.Fill(SixLabors.ImageSharp.Color.FromRgba(8, 10, 14, 200), new SixLabors.ImageSharp.RectangleF(0, 0, size, barH));
                        for (int i = 0; i < captionLines.Count; i++)
                            x.DrawText(captionLines[i], f2, SixLabors.ImageSharp.Color.White,
                                       new SixLabors.ImageSharp.PointF(12f * capScale, 7f * capScale + lineH * i));
                    });
                }
                else
                    Console.WriteLine("   caption: no system font found; image saved without caption");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            img.SaveAsPng(outPath);
        }

        foreach (var m in meshes) m.Dispose();
        GL.DeleteTexture(orange);
        Console.WriteLine($"   textured: {shapes:N0} shapes ({texturedShapes:N0} with a real diffuse), {missingMesh:N0} refs without a resolvable mesh"
                        + $"; {size}x{size}, {(samples >= 2 ? samples + "x msaa" : "no msaa")}");
        if (fxStaticShapes > 0)
            Console.WriteLine($"   static effect shapes: {fxStaticShapes:N0} DRAWN on {fxStaticRefCount:N0} ref(s) "
                            + "— --fx-static, frozen at the authored frame, additive, no depth write");
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
        return shapes;
    }

    /// <summary>Overlay geometry that is not a navmesh edge: the dashed cell outline and the
    /// diff spheres. Vertices are 9 floats — position, colour, normal — where a ZERO normal means
    /// "flat, exactly this colour" and a real one means "shade me".</summary>
    static class Overlay
    {
        public static void Vertex(List<float> dst, Vector3 p, Vector3 colour, Vector3 normal)
        {
            dst.Add(p.X); dst.Add(p.Y); dst.Add(p.Z);
            dst.Add(colour.X); dst.Add(colour.Y); dst.Add(colour.Z);
            dst.Add(normal.X); dst.Add(normal.Y); dst.Add(normal.Z);
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
}
