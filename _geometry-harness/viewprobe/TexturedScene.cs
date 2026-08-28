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
        // the slope-tint pseudocolour (greener flats, greyer steeps). Opaque, depth-written.
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
                // still reads. Fallback: the original pseudocolour.
                var col = textured
                    ? Vector4.Lerp(new Vector4(0.75f, 0.74f, 0.72f, 1f), new Vector4(1f, 1f, 1f, 1f), flatness * flatness)
                    : Vector4.Lerp(new Vector4(0.42f, 0.40f, 0.36f, 1f), new Vector4(0.34f, 0.46f, 0.28f, 1f), flatness * flatness);
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
            Console.WriteLine($"   terrain paint: {terrainTexturedPatches} textured patch(es), {terrainFallbackPatches} pseudocolour fallback");

        // The navmesh overlay — the probe's whole point. Three looks:
        //   filled  the translucent sheet (the original);
        //   wire    every triangle outlined SOLID with no fill (in-plane ribbons that depth-test
        //           like real geometry);
        //   both    a faint sheet plus the outlines.
        //
        // COLOUR. Red is RESERVED for the finding. The healthy colour defaults to "auto": the
        // world is rendered once WITHOUT the overlay, the pixels underneath the navmesh are
        // sampled, and the palette colour farthest from both that background and the reserved
        // red wins — green on snow, cyan/magenta on grass, etc. --nav-color <name> forces one.
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
        GlMesh BuildWire(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> tris, Vector4 color, float halfWidth, string name)
        {
            var wv = new List<Vector3>(); var wi = new List<int>();
            void Ribbon(Vector3 p, Vector3 q, Vector3 n)
            {
                var dir = q - p;
                if (dir.LengthSquared() < 1e-6f) return;
                dir = Vector3.Normalize(dir);
                var side = Vector3.Cross(n, dir);
                if (side.LengthSquared() < 1e-6f) side = Vector3.UnitZ;
                side = Vector3.Normalize(side) * halfWidth;
                int at = wv.Count;
                wv.Add(YUp(p - side + lift)); wv.Add(YUp(p + side + lift));
                wv.Add(YUp(q + side + lift)); wv.Add(YUp(q - side + lift));
                wi.Add(at); wi.Add(at + 1); wi.Add(at + 2);
                wi.Add(at); wi.Add(at + 2); wi.Add(at + 3);
            }
            foreach (var (a, b, c) in tris)
            {
                var n = Vector3.Cross(b - a, c - a);
                n = n.LengthSquared() > 1e-10f ? Vector3.Normalize(n) : Vector3.UnitZ;
                Ribbon(a, b, n); Ribbon(b, c, n); Ribbon(c, a, n);
            }
            var flat = new Vector3[wv.Count]; var uv = new Vector2[wv.Count];
            var col = new Vector4[wv.Count];
            for (int i = 0; i < wv.Count; i++) { flat[i] = Vector3.UnitY; col[i] = color; }
            var mm = new GlMesh();
            mm.Upload(Interleave(wv.ToArray(), flat, uv, flat, flat, col), wi.ToArray());
            mm.ShapeName = name;
            mm.DiffuseTexture = textures.WhiteTexture;
            mm.HasVertexColors = true;
            mm.IsDoubleSided = true;
            return mm;
        }

        var red = new Vector4(1f, 0.12f, 0.05f, 1f);
        void AddNavMeshes(Vector4 healthy)
        {
            bool fill = navMode is "filled" or "both";
            bool wire = navMode is "wire" or "both";
            if (fill)
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
            if (wire && navTris.Count > 0)
            {
                var m0 = BuildWire(navTris, healthy, 2.2f, "navmesh-wire");
                renderer.AddMesh(m0); meshes.Add(m0);
            }
            // The finding is ALWAYS outlined in red — wider than the healthy wire, in every mode
            // that draws navmesh at all.
            if (redTris.Count > 0)
            {
                var m1 = BuildWire(redTris, red, 3.4f, "navmesh-wire-red");
                renderer.AddMesh(m1); meshes.Add(m1);
            }
        }

        // First-person camera: eye/look given in Skyrim coordinates, expressed on the OrbitCamera.
        var eyeY = YUp(cam.Eye); var lookY = YUp(cam.Look);
        var off = eyeY - lookY;
        float dist = MathF.Max(off.Length(), 1f);
        var camera = new OrbitCamera
        {
            Target = new GlVec3(lookY.X, lookY.Y, lookY.Z),
            Distance = dist,
            Elevation = float.RadiansToDegrees(MathF.Asin(off.Y / dist)),
            Azimuth = float.RadiansToDegrees(MathF.Atan2(off.X, off.Z)),
            FieldOfView = cam.FovDeg,
            NearPlane = 4f,
            FarPlane = 131072f,
            MaxDistance = 1_000_000f,
        };

        // Offscreen target + readback (same pattern as the flat path).
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

        byte[] Draw()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.Viewport(0, 0, size, size);
            GL.ClearColor(0.35f, 0.44f, 0.55f, 1f);      // sky-ish ground, not the diagnostic charcoal
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            renderer.Render(camera, size, size);
            GL.Finish();
            var px = new byte[size * size * 4];
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.UnsignedByte, px);
            return px;
        }

        bool wantNav = (navTris.Count > 0 || redTris.Count > 0) && navMode != "off";
        var palette = new (string Name, Vector4 Color)[]
        {
            ("green",   new Vector4(0.05f, 0.85f, 0.30f, 1f)),
            ("cyan",    new Vector4(0.05f, 0.85f, 0.95f, 1f)),
            ("magenta", new Vector4(0.90f, 0.20f, 0.95f, 1f)),
            ("yellow",  new Vector4(1f, 0.88f, 0.10f, 1f)),
            ("blue",    new Vector4(0.25f, 0.45f, 1f, 1f)),
        };

        byte[] pixels;
        if (wantNav && navColor == "auto" && navTris.Count > 0)
        {
            // Pass 1: the world without the overlay. Sample the background exactly where the
            // healthy navmesh will land, by pushing its triangle centroids through the same
            // camera (LookAt + vertical-FOV perspective; GL buffer origin is bottom-left, which
            // matches NDC directly since we sample BEFORE the vertical flip).
            var bg = Draw();
            var view = Matrix4x4.CreateLookAt(eyeY, lookY, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(cam.FovDeg * MathF.PI / 180f, 1f, 4f, 131072f);
            var vp = view * proj;
            float sr = 0, sg = 0, sb = 0; int hits = 0;
            int stride = Math.Max(1, navTris.Count / 600);
            for (int i = 0; i < navTris.Count; i += stride)
            {
                var (a, b, c) = navTris[i];
                var cen = YUp((a + b + c) / 3f + lift);
                var clip = Vector4.Transform(new Vector4(cen, 1f), vp);
                if (clip.W <= 0.01f) continue;
                float nx = clip.X / clip.W, ny = clip.Y / clip.W, nz = clip.Z / clip.W;
                if (nx < -1f || nx > 1f || ny < -1f || ny > 1f || nz < -1f || nz > 1f) continue;
                int px = Math.Clamp((int)((nx * 0.5f + 0.5f) * size), 0, size - 1);
                int py = Math.Clamp((int)((ny * 0.5f + 0.5f) * size), 0, size - 1);
                int o = (py * size + px) * 4;
                sr += bg[o] / 255f; sg += bg[o + 1] / 255f; sb += bg[o + 2] / 255f; hits++;
            }
            string chosen = "green"; var healthy = palette[0].Color;
            if (hits > 0)
            {
                var mean = new Vector3(sr / hits, sg / hits, sb / hits);
                float best = float.MinValue;
                foreach (var (name, colr) in palette)
                {
                    var c3 = new Vector3(colr.X, colr.Y, colr.Z);
                    // Contrast with what's under the mesh, plus distance from the reserved red.
                    float score = (c3 - mean).Length() + 0.5f * (c3 - new Vector3(red.X, red.Y, red.Z)).Length();
                    if (score > best) { best = score; chosen = name; healthy = colr; }
                }
                Console.WriteLine($"   nav colour: auto -> {chosen} (sampled {hits} points under the mesh, "
                                + $"background {(int)(mean.X * 255)},{(int)(mean.Y * 255)},{(int)(mean.Z * 255)})");
            }
            else
                Console.WriteLine("   nav colour: auto -> green (mesh not on screen; nothing to sample)");
            AddNavMeshes(healthy);
            pixels = Draw();
        }
        else
        {
            if (wantNav)
            {
                var pick = palette.FirstOrDefault(p => p.Name == navColor);
                AddNavMeshes(pick.Name is null ? palette[0].Color : pick.Color);
            }
            pixels = Draw();
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DeleteFramebuffer(fbo); GL.DeleteTexture(colorTex); GL.DeleteRenderbuffer(depthRb);

        using (var img = Image.LoadPixelData<Rgba32>(pixels, size, size))
        {
            img.Mutate(x => x.Flip(FlipMode.Vertical));
            if (captionLines.Count > 0)
            {
                // Burn the caption into the frame so it travels with the PNG into any viewer.
                Font? font = null;
                foreach (var famName in new[] { "Segoe UI", "Arial", "Verdana" })
                    if (SystemFonts.TryGet(famName, out var fam)) { font = fam.CreateFont(15f, FontStyle.Bold); break; }
                if (font is null)
                    foreach (var fam in SystemFonts.Families) { font = fam.CreateFont(15f, FontStyle.Bold); break; }
                if (font is not null)
                {
                    int lineH = 22, barH = 10 + lineH * captionLines.Count;
                    var f2 = font;
                    img.Mutate(x =>
                    {
                        x.Fill(SixLabors.ImageSharp.Color.FromRgba(8, 10, 14, 200), new SixLabors.ImageSharp.RectangleF(0, 0, size, barH));
                        for (int i = 0; i < captionLines.Count; i++)
                            x.DrawText(captionLines[i], f2, SixLabors.ImageSharp.Color.White, new SixLabors.ImageSharp.PointF(12, 7 + lineH * i));
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
        Console.WriteLine($"   textured: {shapes:N0} shapes ({texturedShapes:N0} with a real diffuse), {missingMesh:N0} refs without a resolvable mesh");
        if (textures.MissingTexturePaths.Count > 0)
            Console.WriteLine($"   textures the resolver could NOT find (white fallback, counted per rule 6): {textures.MissingTexturePaths.Count:N0}");
        return shapes;
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
