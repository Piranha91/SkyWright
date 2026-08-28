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
        string navMode,                                    // off | filled | wire | both
        string navColor,                                   // auto | green | cyan | magenta | yellow | blue
        Vector3 skyColor,                                  // what the frame clears to (0..1 RGB)
        Func<float, float, float?>? terrainHeightAt,       // winning LAND surface Z at a world X,Y
        bool dashUnderground,                              // dash navmesh buried under the terrain
        bool dashUnderStatics,                             // dash navmesh hidden behind geometry
        int msaa,                                          // multisample count; < 2 = off
        IReadOnlyList<string> captionLines,                // burned into the image, top-left
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
        var meshes = new List<GlMesh>();

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

            // Skyrim-space placement, the SAME composition the detectors use.
            var rot = SpatialOverlap.Euler(r.Rotation, SpatialOverlap.EulerOrder.Xyz);
            var pos = new Vector3(r.Position.X, r.Position.Y, r.Position.Z);

            foreach (var b in built)
            {
                int n = b.Positions.Length;
                if (n == 0 || b.Indices.Length == 0) continue;
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
                mesh.IsModelSpace = b.IsModelSpaceNormals;

                if (r.Highlight)
                {
                    mesh.DiffuseTexture = orange;
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

                renderer.AddMesh(mesh);
                meshes.Add(mesh);
                shapes++;
            }
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
            Elevation = float.RadiansToDegrees(MathF.Asin(camOff.Y / camDist)),
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
                renderer.AddMesh(m0); meshes.Add(m0);
            }
            if (redTris.Count > 0)
            {
                var m1 = BuildFill(redTris, new Vector4(0.95f, 0.15f, 0.08f, MathF.Min(0.55f, alpha + 0.2f)), "navmesh-fill-red");
                renderer.AddMesh(m1); meshes.Add(m1);
            }
        }

        // ---- the wire, classified step by step ------------------------------------------
        const int Visible = 0, Subterranean = 1, Hidden = 2;
        int runVisible = 0, runUnder = 0, runHidden = 0;

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
            return mine > scene * 1.02f + 3f ? Hidden : Visible;
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
                void V(Vector3 v) { dst.Add(v.X); dst.Add(v.Y); dst.Add(v.Z); dst.Add(colour.X); dst.Add(colour.Y); dst.Add(colour.Z); }
                V(a - side); V(a + side); V(b + side);
                V(a - side); V(b + side); V(b - side);
            }
            void Run(int cls, float t0, float t1)
            {
                if (t1 - t0 < 1e-4f) return;
                if (cls == Visible) runVisible++; else if (cls == Subterranean) runUnder++; else runHidden++;
                bool dashed = (cls == Subterranean && dashUnderground) || (cls == Hidden && dashUnderStatics);
                if (!dashed) { Ribbon(solid, t0, t1); return; }
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
        var layers = new List<(float[] Data, bool DepthTested)>();
        if (wantNav)
        {
            if (wireMode && navTris.Count > 0)
            {
                var (s, d) = BuildWire(navTris, healthy, 2.2f);
                if (s.Count > 0) layers.Add((s.ToArray(), true));
                if (d.Count > 0) layers.Add((d.ToArray(), false));
            }
            // The finding is ALWAYS outlined, wider than the healthy wire, in every mode that
            // draws navmesh at all.
            if (redTris.Count > 0)
            {
                var (s, d) = BuildWire(redTris, red, 3.4f);
                if (s.Count > 0) layers.Add((s.ToArray(), true));
                if (d.Count > 0) layers.Add((d.ToArray(), false));
            }
            if (runVisible + runUnder + runHidden > 0)
            {
                string dashing = dashUnderground && dashUnderStatics ? "underground + behind-geometry"
                               : dashUnderground ? "underground only"
                               : dashUnderStatics ? "behind-geometry only" : "nothing (dashes off)";
                Console.WriteLine($"   nav wire: {runVisible:N0} visible run(s), {runUnder:N0} subterranean, "
                                + $"{runHidden:N0} behind geometry; dashed: {dashing}");
            }
        }

        // The overlay pass itself: viewprobe's own flat shader, so an outline is the exact
        // palette colour rather than whatever the scene lighting does to it, and so the hidden
        // layers can drop the depth test.
        int wireProg = 0, wireVao = 0, wireVbo = 0;
        if (layers.Count > 0)
        {
            wireProg = OverlayShader();
            wireVao = GL.GenVertexArray(); wireVbo = GL.GenBuffer();
            GL.BindVertexArray(wireVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, wireVbo);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);
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
                GL.Disable(EnableCap.CullFace);          // ribbons are two-sided by construction
                GL.DepthMask(false);
                foreach (var (data, depthTested) in layers)
                {
                    if (depthTested) { GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Lequal); }
                    else GL.Disable(EnableCap.DepthTest);
                    GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StreamDraw);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, data.Length / 6);
                }
                GL.Enable(EnableCap.DepthTest);
                GL.Enable(EnableCap.CullFace);
                GL.DepthMask(true);
                GL.BindVertexArray(0);
            };
        }

        var pixels = Draw();
        if (wireProg != 0) { GL.DeleteProgram(wireProg); GL.DeleteVertexArray(wireVao); GL.DeleteBuffer(wireVbo); }

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
        if (textures.MissingTexturePaths.Count > 0)
            Console.WriteLine($"   textures the resolver could NOT find (white fallback, counted per rule 6): {textures.MissingTexturePaths.Count:N0}");
        return shapes;
    }

    /// <summary>The wire overlay's own program: position + colour straight through, no lighting.
    /// A diagnostic outline should be the colour it says it is, and the always-visible layers need
    /// a draw the library's mesh path cannot express (depth test off, per layer).</summary>
    static int OverlayShader()
    {
        const string vs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aCol;
uniform mat4 u_view;
uniform mat4 u_projection;
out vec3 vCol;
void main(){ vCol = aCol; gl_Position = u_projection * u_view * vec4(aPos, 1.0); }";

        // x1.5, clamped: the palette entries are the values the auto-colour scorer reasons about,
        // and the library's flat-normal lighting used to land them on screen about half a stop
        // brighter. Keeping that keeps a cyan outline the same cyan it has always been.
        const string fs = @"#version 330 core
in vec3 vCol;
out vec4 frag;
void main(){ frag = vec4(min(vCol * 1.5, vec3(1.0)), 1.0); }";

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
