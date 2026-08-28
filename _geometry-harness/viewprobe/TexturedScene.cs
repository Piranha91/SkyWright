using System.Numerics;
using CharacterViewer.Rendering;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using OpenTK.Graphics.OpenGL4;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
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
        string navMode,                                    // off | filled | wire | both
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

        // The navmesh overlay — the probe's whole point. Three looks:
        //   filled  the translucent green sheet (the original);
        //   wire    every triangle outlined SOLID with no fill, so the surface underneath stays
        //           readable — the outlines are thin in-plane ribbons, not GL lines, so they
        //           depth-test like real geometry;
        //   both    a faint sheet plus the outlines.
        if (navTris.Count > 0 && navMode != "off")
        {
            bool fill = navMode is "filled" or "both";
            bool wire = navMode is "wire" or "both";
            var lift = new Vector3(0, 0, 2f);                           // Z-up lift before the swap

            if (fill)
            {
                float alpha = navMode == "both" ? 0.16f : 0.35f;
                var nv = new List<Vector3>(); var ni = new List<int>();
                foreach (var (a, b, c) in navTris)
                {
                    int at = nv.Count;
                    nv.Add(YUp(a + lift)); nv.Add(YUp(b + lift)); nv.Add(YUp(c + lift));
                    ni.Add(at); ni.Add(at + 1); ni.Add(at + 2);
                }
                var flat = new Vector3[nv.Count]; var uv = new Vector2[nv.Count];
                var col = new Vector4[nv.Count];
                for (int i = 0; i < nv.Count; i++) { flat[i] = Vector3.UnitY; col[i] = new Vector4(0.2f, 0.95f, 0.45f, alpha); }
                var navMesh = new GlMesh();
                navMesh.Upload(Interleave(nv.ToArray(), flat, uv, flat, flat, col), ni.ToArray());
                navMesh.ShapeName = "navmesh-fill";
                navMesh.DiffuseTexture = textures.WhiteTexture;
                navMesh.HasVertexColors = true;
                navMesh.HasAlphaBlend = true;
                navMesh.MaterialAlpha = alpha;
                navMesh.IsDoubleSided = true;
                navMesh.DepthWrite = false;
                renderer.AddMesh(navMesh);
                meshes.Add(navMesh);
            }

            if (wire)
            {
                const float halfW = 2.2f;                               // ribbon half-width, world units
                var wv = new List<Vector3>(); var wi = new List<int>();
                void Ribbon(Vector3 p, Vector3 q, Vector3 n)
                {
                    var dir = q - p;
                    if (dir.LengthSquared() < 1e-6f) return;
                    dir = Vector3.Normalize(dir);
                    var side = Vector3.Cross(n, dir);
                    if (side.LengthSquared() < 1e-6f) side = Vector3.UnitZ;
                    side = Vector3.Normalize(side) * halfW;
                    int at = wv.Count;
                    wv.Add(YUp(p - side + lift)); wv.Add(YUp(p + side + lift));
                    wv.Add(YUp(q + side + lift)); wv.Add(YUp(q - side + lift));
                    wi.Add(at); wi.Add(at + 1); wi.Add(at + 2);
                    wi.Add(at); wi.Add(at + 2); wi.Add(at + 3);
                }
                foreach (var (a, b, c) in navTris)
                {
                    var n = Vector3.Cross(b - a, c - a);
                    n = n.LengthSquared() > 1e-10f ? Vector3.Normalize(n) : Vector3.UnitZ;
                    Ribbon(a, b, n); Ribbon(b, c, n); Ribbon(c, a, n);
                }
                var flat = new Vector3[wv.Count]; var uv = new Vector2[wv.Count];
                var col = new Vector4[wv.Count];
                for (int i = 0; i < wv.Count; i++) { flat[i] = Vector3.UnitY; col[i] = new Vector4(0.05f, 0.85f, 0.30f, 1f); }
                var edges = new GlMesh();
                edges.Upload(Interleave(wv.ToArray(), flat, uv, flat, flat, col), wi.ToArray());
                edges.ShapeName = "navmesh-wire";
                edges.DiffuseTexture = textures.WhiteTexture;
                edges.HasVertexColors = true;
                edges.IsDoubleSided = true;
                renderer.AddMesh(edges);
                meshes.Add(edges);
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

        GL.Viewport(0, 0, size, size);
        GL.ClearColor(0.35f, 0.44f, 0.55f, 1f);          // sky-ish ground, not the diagnostic charcoal
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        renderer.Render(camera, size, size);
        GL.Finish();

        var pixels = new byte[size * size * 4];
        GL.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DeleteFramebuffer(fbo); GL.DeleteTexture(colorTex); GL.DeleteRenderbuffer(depthRb);

        using (var img = Image.LoadPixelData<Rgba32>(pixels, size, size))
        {
            img.Mutate(x => x.Flip(FlipMode.Vertical));
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
