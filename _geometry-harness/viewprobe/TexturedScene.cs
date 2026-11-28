using System.Numerics;
using SkyRender;
using SkyRender.Character;
using SkyRender.World;
using HousecarlCore;
using SkyWright.Engine;
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
            if (bytes is null)
            {
                error = $"'{subpath}' not present in '{Path.GetFileName(containingBsaPath)}'";
                // LOCATED IN AN ARCHIVE AND STILL NOT READABLE. The library turns this into the
                // same "not found" that an asset nobody shipped produces, so it is recorded here,
                // where the difference is still visible.
                UnreadableAssets.ExtractionFailed(subpath, containingBsaPath, error);
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.WriteAllBytes(destPath, bytes);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            UnreadableAssets.ExtractionFailed(subpath, containingBsaPath, error);
            return false;
        }
    }
}

sealed class ConsoleCvLogger : IRenderLogger
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
    public IList<LightingLayout> UserLightingLayouts { get; } = new List<LightingLayout>();
    public IList<LightingColorScheme> UserLightingColorSchemes { get; } = new List<LightingColorScheme>();
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
/// Say so in the docs rather than implying the curve is the engine's. That fitted ramp now serves
/// only the multi-pass shell: a SINGLE-PASS material is drawn per pixel inside the base shape's own
/// shader with the engine's formula (SkyRender's projected material; see the per-shape block).</summary>
readonly record struct Covering(Vector3 ProjectionVector,     // Skyrim space, direction of travel
                                float MaxAngleDeg,            // STAT.MaxAngle — the hard gate
                                float FalloffBias, float FalloffScale,
                                float UvScale,                // MATO.MaterialUvScale, world u per tile
                                Vector4 Color,                // SinglePassColor, or white when a texture carries it
                                string? ModelPath,            // the MATO's own model NIF; its slot-0 diffuse IS the covering texture
                                string Label,                 // MATO EditorID, for the census
                                bool SinglePass,              // MATO flag: drawn by the base's own shader, not a second shell
                                float NoiseUvScale,           // MATO.NoiseUvScale, world u per noise tile (single pass)
                                float DiffuseTiling,          // fProjectedUVDiffuseNormalTilingScale (SkyrimPrefs.ini)
                                bool ProjectedDiffuse);       // bEnableProjecteUVDiffuseNormals (SkyrimPrefs.ini)

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
                                  string Label,
                                  float FogNear, float FogFar, float FogAmount,   // WATR above-water fog (depth fog)
                                  float NoiseFalloff,                             // WATR.NoiseFalloff (reflection distance blend)
                                  Vector3 Spec);                                  // WATR sun specular power, sun specular magnitude, depth specular lighting

static class TexturedScene
{
    /// <summary>Test-only dependencies around the real mesh miss block. Production leaves this
    /// null and follows the existing resolver/builder/file path. The hook does not own or retain
    /// the per-shot cache; the RED gate uses that fact to observe today's lifetime.</summary>
    internal sealed class MeshReadHooks
    {
        public required Func<string, string?> Resolve { get; init; }
        public required Func<string, List<NifMeshBuilder.BuiltMesh>> Build { get; init; }
        public required Func<string, byte[]> ReadBytes { get; init; }
        public Action<string, IReadOnlyList<NifMeshBuilder.BuiltMesh>?, string?, string?, string, string>? Observe { get; init; }
        public Action<int, int>? ObserveCull { get; init; }
        public Action? ObserveMeshStateFactory { get; init; }
    }

    /// <summary>The owner-release scope for resources a worklist shot creates outside RenderToPng and owns for the
    /// whole shot (the NPC actors' VMs and their link-cache overlays, which must outlive every panel of the shot).
    /// Declared with `using` where they are created, so an ordinary return and a fault release them the same way,
    /// exactly once (Gate B6 review, Q4: a shot-scoped release must never sit at a site the fault path skips).</summary>
    internal sealed class ShotOwnerRelease(Action release) : IDisposable
    {
        bool released;
        public void Dispose()
        {
            if (released) return;
            released = true;
            release();
        }
    }

    sealed class ShotResourceScope : IDisposable
    {
        readonly GlRenderer renderer; readonly List<GlMesh> owned; readonly List<GlMesh> borrowed;
        readonly GraphicsReuseRed.ShotScope? evidence; readonly List<(string Kind, int Handle)> raw = [];
        readonly bool removeOwnedFromRenderer;
        bool disposed;
        internal ShotResourceScope(GlRenderer renderer, List<GlMesh> owned, List<GlMesh> borrowed,
            GraphicsReuseRed.ShotScope? evidence, bool removeOwnedFromRenderer)
        {
            this.renderer = renderer; this.owned = owned; this.borrowed = borrowed;
            this.evidence = evidence; this.removeOwnedFromRenderer = removeOwnedFromRenderer;
        }
        internal void TrackTexture(int handle) => raw.Add(("texture", handle));
        internal void TrackTargets(int fbo, int color, int depth, int msFbo, int msColor, int msDepth)
        { raw.Add(("framebuffer", fbo)); raw.Add(("texture", color)); raw.Add(("renderbuffer", depth)); raw.Add(("framebuffer", msFbo)); raw.Add(("renderbuffer", msColor)); raw.Add(("renderbuffer", msDepth)); }
        internal void TrackOverlay(int program, int vao, int vbo)
        { raw.Add(("program", program)); raw.Add(("vertex-array", vao)); raw.Add(("buffer", vbo)); }
        internal void Track(string kind, int handle) => raw.Add((kind, handle));
        public void Dispose()
        {
            if (disposed) return; disposed = true; List<Exception> failures = [];
            foreach (var mesh in borrowed)
                try { renderer.RemoveMesh(mesh); evidence?.MeshRemoved("dynamic-npc-borrowed", mesh, borrowed: true); }
                catch (Exception ex) { failures.Add(ex); }
            borrowed.Clear();
            foreach (var mesh in owned)
            {
                if (removeOwnedFromRenderer)
                    try { renderer.RemoveMesh(mesh); evidence?.MeshRemoved("dynamic-owned", mesh); }
                    catch (Exception ex) { failures.Add(ex); }
                try { mesh.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            }
            owned.Clear();
            try { evidence?.RendererMembership("shot-end", renderer); } catch (Exception ex) { failures.Add(ex); }   // G3 RED
            if (raw.Any(x => x.Kind == "framebuffer" && x.Handle > 0))
                try { GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); }
                catch (Exception ex) { failures.Add(ex); }
            // A program or vertex array deleted while still bound is only flagged, and outlives the shot in a
            // retained context. Unbind both before deleting (the overlay's own deletion site does the same).
            if (raw.Any(x => (x.Kind == "program" || x.Kind == "vertex-array") && x.Handle > 0))
                try { GL.UseProgram(0); GL.BindVertexArray(0); }
                catch (Exception ex) { failures.Add(ex); }
            foreach (var (kind, handle) in raw.AsEnumerable().Reverse())
            {
                if (handle <= 0) continue;
                try
                {
                    if (kind == "texture" && GL.IsTexture(handle)) GL.DeleteTexture(handle);
                    else if (kind == "framebuffer" && GL.IsFramebuffer(handle)) GL.DeleteFramebuffer(handle);
                    else if (kind == "renderbuffer" && GL.IsRenderbuffer(handle)) GL.DeleteRenderbuffer(handle);
                    else if (kind == "program" && GL.IsProgram(handle)) GL.DeleteProgram(handle);
                    else if (kind == "vertex-array" && GL.IsVertexArray(handle)) GL.DeleteVertexArray(handle);
                    else if (kind == "buffer" && GL.IsBuffer(handle)) GL.DeleteBuffer(handle);
                    else if (kind == "query" && GL.IsQuery(handle)) GL.DeleteQuery(handle);
                }
                catch (Exception ex) { failures.Add(ex); }
            }
            raw.Clear();
            if (failures.Count > 0) throw new AggregateException("shot graphics teardown failed", failures);
        }
    }

    /// <summary>One mesh read the way the retained mesh table keeps it (CellScene.cs <see cref="MeshState"/>): built,
    /// stripped of its root transform, normalised, with the file's identity. A missing file, a failed build and a failed
    /// identity read are each part of the outcome. GL-free: the live scene's prefetch calls it off the window's thread.</summary>
    internal static (MeshOutcome Outcome, Exception? IdentityFailure) ReadOutcome(string? abs,
        Func<string, List<NifMeshBuilder.BuiltMesh>> build, Func<string, byte[]> readBytes)
    {
        if (abs is null) return (new MeshOutcome(null, "", null, true), null);
        List<NifMeshBuilder.BuiltMesh>? built = null;
        Exception? buildFailure = null, identityFailure = null;
        string hash = "";
        try
        {
            built = build(abs);
            StripNifRootTransform(abs, built);
            built = NormalizeMeshes(built);
        }
        catch (Exception ex) { buildFailure = ex; built = null; }
        try { hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(readBytes(abs))); }
        catch (Exception ex) { identityFailure = ex; }
        return (new MeshOutcome(built, hash, buildFailure, false), identityFailure);
    }

    /// <summary>The live scene's prefetch (LiveScene.cs): read the meshes of <paramref name="refs"/> into the session's mesh
    /// table and get their textures ready, off the window's thread, so the slice build that follows mostly uploads. Under the
    /// decoded texture policy that is a decode into the pixel cache. Under a compressed one (TexturePolicy.cs) a texture the
    /// library uploads compressed needs no decoded pixels, and which ones fall back is the library's rule, decided at upload:
    /// so nothing 2D is decoded or kept here, only each path resolved (the archive lookup and any extraction, off the window's
    /// thread), and the few fallbacks decode on demand at upload, as every CPU use of pixels already does. The environment
    /// map (slot 4) is the exception: it always decodes, so it is still warmed, through the cube-map cache it is read from. Stops early when
    /// cancelled. Returns how many meshes it read.</summary>
    internal static int Prefetch(LiveSession session, IEnumerable<SceneRef> refs, bool parallel, CancellationToken ct)
    {
        int read = 0;
        bool decode = session.Assets.Cache.DdsMaximumFinalMipDimension is null;   // the cache's frozen policy: decoded
        var rels = refs.Select(r => r.MeshRelPath.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? r.MeshRelPath : Path.Combine("meshes", r.MeshRelPath))
                       .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // parallel (half the cores) only while nothing is drawn; beside a drawing window one thread, which measured smoother
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = parallel ? Math.Max(1, Environment.ProcessorCount / 2) : 1 };
        try { Parallel.ForEach(rels, options, rel => { if (One(rel)) Interlocked.Increment(ref read); }); }
        catch (OperationCanceledException) { }
        return read;

        bool One(string rel)
        {
            bool made = false;
            if (!session.Mesh.TryGet(rel, out var outcome))
            {
                var (read1, identityFailure) = ReadOutcome(session.Assets.Resolver.ResolveAssetPath(rel), p => session.Mesh.Builder.BuildFromFile(p), File.ReadAllBytes);
                outcome = read1;
                if (read1.MissingPath || read1.Failure is not null || identityFailure is null) session.Mesh.Store(rel, read1);
                made = true;
            }
            var cache = session.Assets.Cache;
            foreach (var b in outcome.Built ?? new List<NifMeshBuilder.BuiltMesh>())
                foreach (var (slot, t) in b.TexturePaths)
                    if (t is { Length: > 0 } && !ct.IsCancellationRequested)
                        try
                        {
                            if (decode) cache.GetOrLoadDdsPixels(t);
                            // slot 4, the environment map, always decodes (a cube map, else a 2D map): warmed here as the
                            // library's own prewarm does, so that decode stays off the window's thread too
                            else if (slot == 4) { if (cache.GetOrLoadDdsCubemap(t) is null) cache.GetOrLoadDdsPixels(t); }
                            else cache.ResolveAssetPath(t);
                        }
                        catch { /* the slice build reports what it cannot read */ }
            return made;
        }
    }

    static List<NifMeshBuilder.BuiltMesh>? NormalizeMeshes(List<NifMeshBuilder.BuiltMesh>? built)
    {
        if (built is null) return null;
        return built.Select(b => new NifMeshBuilder.BuiltMesh
        {
            Positions = (Vector3[])b.Positions.Clone(), Normals = (Vector3[])b.Normals.Clone(),
            Indices = (int[])b.Indices.Clone(), TextureCoordinates = (Vector2[])b.TextureCoordinates.Clone(),
            Tangents = (Vector3[])b.Tangents.Clone(), Bitangents = (Vector3[])b.Bitangents.Clone(),
            VertexColors = b.VertexColors is null ? null : (Vector4[])b.VertexColors.Clone(),
            ShapeName = b.ShapeName, TexturePaths = new Dictionary<int, string>(b.TexturePaths),
            HasAlphaTest = b.HasAlphaTest, AlphaThreshold = b.AlphaThreshold,
            HasAlphaBlend = b.HasAlphaBlend, MaterialAlpha = b.MaterialAlpha,
            ZBufferWrite = b.ZBufferWrite, IsDecal = b.IsDecal,
            IsDoubleSided = b.IsDoubleSided, IsModelSpaceNormals = b.IsModelSpaceNormals,
            SrcBlendIndex = b.SrcBlendIndex, DstBlendIndex = b.DstBlendIndex,
            HasEffectShader = b.HasEffectShader, EffectBaseColor = b.EffectBaseColor,
            EffectBaseColorAlpha = b.EffectBaseColorAlpha, EffectBaseColorScale = b.EffectBaseColorScale,
            EffectShaderFlags1 = b.EffectShaderFlags1, EffectShaderFlags2 = b.EffectShaderFlags2,
            EffectGreyscaleTexture = b.EffectGreyscaleTexture, EffectFalloff = b.EffectFalloff,
            EffectSoftFalloffDepth = b.EffectSoftFalloffDepth, EffectLightingInfluence = b.EffectLightingInfluence,
            EffectUvScale = b.EffectUvScale, EffectUvOffset = b.EffectUvOffset,
            EffectTextureClampMode = b.EffectTextureClampMode, HasVertexColors = b.HasVertexColors, BillboardMode = b.BillboardMode,
            BillboardFrame = b.BillboardFrame,
            EmissiveColor = b.EmissiveColor, EmissiveMultiple = b.EmissiveMultiple,
            EnvironmentMapScale = b.EnvironmentMapScale, EyeCubemapScale = b.EyeCubemapScale,
            ShaderFlags1 = b.ShaderFlags1, ShaderFlags2 = b.ShaderFlags2, ShaderType = b.ShaderType,
        }).ToList();
    }

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

    internal static string StaticInputKey(string preparedIdentity, IReadOnlyList<SceneRef> refs,
        IReadOnlyDictionary<string, List<(Vector3 A, Vector3 B, Vector3 C)>> terrainPatches,
        IReadOnlyList<(string Diffuse, List<(Vector3 A, Vector3 B, Vector3 C, float Oa, float Ob, float Oc)> Tris)> terrainLayers,
        IReadOnlyDictionary<FormKey, Covering> coverings, bool keepFx, bool fxStatic,
        float? cullAbove, bool refSurface, float refSurfaceOpacity, string texturePolicy)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Add(string? value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? "<null>");
            hash.AppendData(bytes); hash.AppendData(new byte[] { 0 });
        }
        static string F(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        static string V(Vector3 value) => $"{F(value.X)},{F(value.Y)},{F(value.Z)}";
        static string H(Vec3 value) => $"{F(value.X)},{F(value.Y)},{F(value.Z)}";
        Add(preparedIdentity); Add($"fx={keepFx};fxStatic={fxStatic};cull={cullAbove?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};surface={refSurface};opacity={F(refSurfaceOpacity)}");
        Add(Environment.GetEnvironmentVariable("VP_LAYER_ONLY")); Add(Environment.GetEnvironmentVariable("VP_LAYER_SOLID"));
        Add(Environment.GetEnvironmentVariable("VP_FX_DOUBLESIDED"));
        Add("textures=" + texturePolicy);   // TexturePolicy.cs: the uploaded textures differ by policy
        foreach (var r in refs)
            Add(string.Join("|", r.MeshRelPath, r.RefKey, r.BaseKey, r.BaseEditorId, H(r.Position), H(r.Rotation),
                F(r.Scale), r.Highlight, r.Marker, r.BosRecordBase, r.BosLabel, r.BosRandom, r.StateChange,
                r.StateLabel, r.Constructed, V(r.HighlightColor)));
        foreach (var patch in terrainPatches.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            Add("patch:" + patch.Key);
            foreach (var (a, b, c) in patch.Value) Add(V(a) + "|" + V(b) + "|" + V(c));
        }
        foreach (var layer in terrainLayers)
        {
            Add("layer:" + layer.Diffuse);
            foreach (var (a, b, c, oa, ob, oc) in layer.Tris)
                Add(V(a) + "|" + V(b) + "|" + V(c) + "|" + F(oa) + "|" + F(ob) + "|" + F(oc));
        }
        foreach (var pair in coverings.OrderBy(x => x.Key))
        {
            var c = pair.Value;
            Add(string.Join("|", pair.Key, V(c.ProjectionVector), F(c.MaxAngleDeg), F(c.FalloffBias),
                F(c.FalloffScale), F(c.UvScale), F(c.Color.X), F(c.Color.Y), F(c.Color.Z), F(c.Color.W),
                c.ModelPath, c.Label, c.SinglePass, F(c.NoiseUvScale), F(c.DiffuseTiling), c.ProjectedDiffuse));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
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
        int sizeHeight = 0,                             // capture canvas HEIGHT in pixels; 0 keeps the historic square (sizeW x sizeW)
        CellScene? cellScene = null,                    // --reuse-cell: this CELL's lease (CellScene.cs); null = build everything for this shot, as before
        MeshReadHooks? meshReadHooks = null,            // RED instrumentation only; null is the production path
        GraphicsReuseRed? graphicsReuseRed = null,      // private graphics-lifetime RED; null is the production path
        GraphicsCell? graphicsCell = null,              // opt-in retained GL owner; null preserves the per-shot path
        string? preparedIdentity = null,                // retained CPU-walk identity; required with graphicsCell
        DepthMeasurement? depthMeasurement = null,      // --depth-measurement: only alpha-tested diffuses load (DepthMeasurement.cs)
        NativeTerrainExtras? terrainExtras = null,      // LAND normals / colours and TXST normal maps for the native terrain
        LiveSession? live = null,                       // a cell slice built into a live scene (LiveScene.cs); null = a whole build
        List<GlMesh>? liveMeshes = null)                // slice mode: receives the meshes the slice added, for the caller to own
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
        using var graphicsShot = graphicsReuseRed?.BeginShot(new GraphicsReuseRed.GraphicsShotFacts(
            CameraSha256: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(FormattableString.Invariant(
                    $"{cam.Eye.X:R}|{cam.Eye.Y:R}|{cam.Eye.Z:R}|{cam.Look.X:R}|{cam.Look.Y:R}|{cam.Look.Z:R}|{cam.FovDeg:R}|{sizeW}|{sizeH}")))),
            StaticRefPopulation: refs.Count,
            TerrainPatchPopulation: terrainPatches.Sum(x => x.Value.Count),
            TerrainLayerPopulation: terrainLayers.Sum(x => x.Tris.Count),
            WaterPopulation: waterPlanes.Count,
            NavTrianglePopulation: navTris.Count,
            RedTrianglePopulation: redTris.Count,
            NpcPopulation: npcActors.Count,
            NpcPuckPopulation: npcPucks ? npcActors.Count : 0,
            RoutePopulation: routes.Count,
            GridPopulation: grid is null ? 0 : 1,
            OverlayLinePopulation: overlayLines.Count,
            PlanePopulation: planes.Count,
            SubjectMaskPopulation: refs.Count(x => x.Highlight)));
        var logger = new ConsoleCvLogger();
        var gate = new RenderLogGate { Verbose = Environment.GetEnvironmentVariable("VP_CV_VERBOSE") == "1" };

        var folders = ResolverFolders(modFoldersHighestFirst, overwriteDir);

        // The bracket. GameAssetResolver captures Path.Combine(AppContext.BaseDirectory,
        // "SkyRenderCache") in its constructor and never re-reads it, so this is the one line
        // in the run where the redirect has to be in force — see ExtractionScratch.cs.
        GameAssetResolver MakeResolver()
        {
            GameAssetResolver made;
            using (ExtractionScratch.Redirect())
                made = new GameAssetResolver(new HcDataFolder(dataDir), new HcBsaProvider(assets), gate, logger);
            made.SetAdditionalFolders(folders);
            return made;
        }
        // What the cell lease holds (CellScene.cs) — the resolver and the cache of pixels it
        // decodes, made TOGETHER because the cache captures the resolver and resolves every key
        // through it. Both are shot-independent: the resolver's inputs are process-level and the
        // cache keys on the resolved disk path, while their instance state is exactly the work
        // repeated today — the second shot of a cell re-extracts from the archives, and re-decodes
        // through Pfim, everything the first shot already did. With no lease these are the same
        // two lines they always were, built here, for this shot.
        CellAssets MakeAssets()
        {
            var made = MakeResolver();
            var madeCache = new AssetCache(made, logger, gate, new CvSettings());
            // the cache froze the policy from the environment; the pictures it feeds carry TexturePolicy.Current's token
            TexturePolicy.Current.RequireSameAs(madeCache);
            Console.WriteLine($"   texture policy: {TexturePolicy.Current.Label} ({TexturePolicy.Current.Source})");
            return new CellAssets(made, madeCache);
        }
        var (resolver, cache) = live is not null ? live.Assets : cellScene is null ? MakeAssets() : cellScene.Assets(MakeAssets);

        MeshState MakeMeshState()
        {
            meshReadHooks?.ObserveMeshStateFactory?.Invoke();
            return new MeshState(new NifMeshBuilder(logger, gate, resolver, new CvSettings()));
        }
        var meshState = live?.Mesh ?? (cellScene is null ? MakeMeshState() : cellScene.Mesh(MakeMeshState));
        var builder = meshState.Builder;
        // Declared before the real GL owners so it disposes after them, while Program's real
        // GameWindow/context is still current. The scope queries handles but owns none of them.
        using var graphicsLifetime = graphicsShot?.BeginGraphicsLifetime();
        if (graphicsCell is not null && string.IsNullOrWhiteSpace(preparedIdentity))
            throw new InvalidOperationException("retained graphics requires the prepared-cell identity");
        string staticKey = graphicsCell is null ? "unretained" : StaticInputKey(preparedIdentity!, refs,
            terrainPatches, terrainLayers, coveringsByBase, keepFx, fxStatic, cullAbove, refSurface, refSurfaceOpacity, TexturePolicy.Current.Token)
            + (depthMeasurement is null ? "" : "|" + DepthMeasurement.Name);   // a retained scene never crosses modes
        // Measurement only (Gate B4, G2 RED): per-component digests of the key's inputs; inert without a RED session.
        if (graphicsCell is not null)
            graphicsShot?.StaticKeyInputs(staticKey, preparedIdentity!, refs, terrainPatches, terrainLayers, coveringsByBase,
                keepFx, fxStatic, cullAbove, refSurface, refSurfaceOpacity);
        BuiltScene? builtScene = graphicsCell?.BeginShot(staticKey, cache, logger, graphicsShot);
        // Shot-local textures never join the cell's resident cache (Gate B2 review, C2 option A): in reuse mode the
        // per-shot manager is built exactly as the no-flag path builds it, so what it loads dies with the shot and
        // the resident cache holds only the retained install textures.
        using var ownTextures = live is null ? new GlTextureManager(cache, logger, null) : null;
        ownTextures?.Initialize();
        var textures = live?.Textures ?? ownTextures!;
        graphicsShot?.TextureManagerReady(textures, initializedNow: true,
            role: graphicsCell is null ? "install" : "shot", cellLifetime: graphicsCell is null);

        using var localRenderer = graphicsCell is null && live is null ? new GlRenderer() : null;
        var renderer = live?.Renderer ?? graphicsCell?.Renderer ?? localRenderer!;
        graphicsShot?.RendererMembership("shot-start", renderer);   // G3 RED, measurement only
        if (localRenderer is not null)
        {
            // Embedded shaders; VP_SHADER_DIR=<folder> loads basic.vert/.frag etc. from disk instead
            // (a diagnostic: try a shader edit without rebuilding).
            renderer.Initialize(Environment.GetEnvironmentVariable("VP_SHADER_DIR"));
            graphicsShot?.RendererReady(renderer, initializedNow: true);
        }
        // The lights, sky and fog are the scene's, set by its first build; a slice adds geometry to them and changes none.
        if (live is null)
        {
            renderer.EnableBloom = false;                    // deterministic output; no post FX
            renderer.EnableShadows = false;
            renderer.ShadowBounds = null;
            renderer.PointLights.Clear();
            renderer.EnableAmbientOcclusion = false;
            // The sky. GlRenderer.Render() clears the frame AGAIN with its own ClearColor, so this
            // is the setting that decides the background — the GL.ClearColor in Draw() below only
            // matters for the instant before the library's clear lands on top of it.
            renderer.ClearColor = new GlVec3(skyColor.X, skyColor.Y, skyColor.Z);
            renderer.SetAmbientIntensity(0.55f);
            renderer.SetKeyLight(azimuthDeg: 135f, elevationDeg: 55f, intensity01: 0.85f, new GlVec3(1f, 0.98f, 0.92f));
            renderer.SetFillLight(azimuthDeg: 315f, elevationDeg: 20f, intensity01: 0.35f, new GlVec3(0.85f, 0.9f, 1f));
            // --weather (SceneWeather.cs): the WTHR's sky, sun, ambient and fog replace the fixed studio
            // lights. The sun is placed from the worldspace's climate (below); there is no fill light in the engine.
            renderer.Fog = null;
            renderer.ImageSpace = null;
            renderer.SkyColor = null;
            renderer.SkyGradient = null;
            renderer.Lights[0].Color = new GlVec3(1f, 1f, 1f);   // a reused renderer may carry a weather's ambient
            if (SceneWeather.Requested is not null && SceneWeather.Current is null)
                Console.WriteLine($"   weather: no WTHR '{SceneWeather.Requested}' in the load order (or this walk was retained) - studio lights kept");
            if (SceneWeather.Current is { } sw)
            {
                if (!SceneWeather.SkyGiven) renderer.ClearColor = new GlVec3(sw.SkyUpper.X, sw.SkyUpper.Y, sw.SkyUpper.Z);
                renderer.SkyColor = new GlVec3(sw.SkyUpper.X, sw.SkyUpper.Y, sw.SkyUpper.Z);
                renderer.Lights[0].Color = new GlVec3(sw.AmbientUp.X, sw.AmbientUp.Y, sw.AmbientUp.Z);
                renderer.SetAmbientIntensity(1f);
                // The image space: the engine scales the sun's colour by its HDR sunlight scale and
                // finishes the frame with its tone map and cinematic grade (SkyRender ImageSpaceFinish).
                var isd = SceneWeather.ImageSpaceOff ? null : SceneWeather.ImageSpaceData;
                // The sun's direction from the worldspace's climate at --game-hour (SceneWeather.SunPath);
                // the fixed studio direction when there is no climate. VP_SUN_FIXED=1 keeps the fixed one.
                var sunPath = Environment.GetEnvironmentVariable("VP_SUN_FIXED") == "1" ? null : SceneWeather.SunPath();
                renderer.SetKeyLight(azimuthDeg: sunPath?.Azimuth ?? 135f, elevationDeg: sunPath?.Elevation ?? 55f,
                                     intensity01: isd?.SunlightScale ?? 1f,
                                     new GlVec3(sw.Sunlight.X, sw.Sunlight.Y, sw.Sunlight.Z));
                Console.WriteLine(sunPath is { } spx ? $"   weather sun: {spx.Text}"
                                                     : "   weather sun: fixed studio direction (azimuth 135, elevation 55) - no climate read");
                if (isd is not null)
                    renderer.ImageSpace = new ImageSpaceFinish(isd.White, isd.Saturation, isd.Brightness, isd.Contrast,
                        new GlVec3(isd.TintColor.X, isd.TintColor.Y, isd.TintColor.Z), isd.TintAmount, SceneWeather.AdaptedLuminance);
                renderer.SetFillLight(azimuthDeg: 315f, elevationDeg: 20f, intensity01: 0f, new GlVec3(1f, 1f, 1f));
                // The sky by elevation (SceneWeather.SkyKeys): what sky-reflecting water reflects, per pixel,
                // and the frame's background (the dome at 20 degrees; the fogged sky is nearly flat).
                // VP_SKY_FLAT=1 keeps the single sky-upper colour; VP_SKY_NO_FOG=1 drops the fog over the sky.
                if (Environment.GetEnvironmentVariable("VP_SKY_FLAT") != "1")
                {
                    var skyKeys = sw.SkyKeys(isd?.SkyScale ?? 0f, fog: Environment.GetEnvironmentVariable("VP_SKY_NO_FOG") != "1");
                    renderer.SkyGradient = new SkyGradient(skyKeys.Select(k => new SkyGradientKey(k.El, new GlVec3(k.Color.X, k.Color.Y, k.Color.Z))).ToArray());
                    if (!SceneWeather.SkyGiven) renderer.ClearColor = renderer.SkyGradient.At(20f);
                    Console.WriteLine($"   weather sky: gradient of {skyKeys.Length} keys, horizon {skyKeys[0].Color:0.000}, 20 deg {renderer.SkyGradient.At(20f):0.000}, zenith {skyKeys[^1].Color:0.000}");
#if SKYRENDER_SKY
                    // The sky itself (SceneSky.cs): the dome by the gradient, the weather's cloud layers, the climate's sun, and
                    // the moons and stars at night. VP_SKY_OFF=1 keeps the plain background.
                    if (Environment.GetEnvironmentVariable("VP_SKY_OFF") != "1")
                    {
                        renderer.Sky = SceneSky.Settings(sw, renderer.SkyGradient, GlWorldFactory.LoaderFor(textures));
                        Console.WriteLine($"   weather sky: {SceneSky.Describe(sw)}");
                    }
#endif
                }
                // Diagnostics for the colour balance (README, VP_WEATHER_*): drop the fog, or scale the ambient.
                if (float.TryParse(Environment.GetEnvironmentVariable("VP_WEATHER_AMBIENT_SCALE"),
                                   System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ambScale))
                    renderer.SetAmbientIntensity(ambScale);
                // VP_WEATHER_AMBIENT_FROM=0..5: take the ambient from another of the six directional
                // ambient colours (+X, -X, +Y, -Y, +Z, -Z) instead of -Z, or 6 = their mean.
                if (int.TryParse(Environment.GetEnvironmentVariable("VP_WEATHER_AMBIENT_FROM"), out var ambFrom)
                    && sw.Ambient6.Length == 6 && ambFrom >= 0 && ambFrom <= 6)
                {
                    var ac = ambFrom == 6 ? (sw.Ambient6[0] + sw.Ambient6[1] + sw.Ambient6[2] + sw.Ambient6[3] + sw.Ambient6[4] + sw.Ambient6[5]) / 6f
                                          : sw.Ambient6[ambFrom];
                    renderer.Lights[0].Color = new GlVec3(ac.X, ac.Y, ac.Z);
                }
                renderer.Fog = Environment.GetEnvironmentVariable("VP_WEATHER_NO_FOG") == "1" ? null : sw.FogFar > sw.FogNear
                    ? new DistanceFog(new GlVec3(sw.FogNearColor.X, sw.FogNearColor.Y, sw.FogNearColor.Z),
                                      new GlVec3(sw.FogFarColor.X, sw.FogFarColor.Y, sw.FogFarColor.Z),
                                      sw.FogNear, sw.FogFar, sw.FogPower, sw.FogMax)
                    : null;
                Console.WriteLine($"   weather: {sw}" + (SceneWeather.CurrentWhy is { } why ? $"  [{why}; --no-weather for the studio lights]" : ""));
                if (SceneWeather.ImageSpaceText is { } ist)
                    Console.WriteLine($"   weather image space ({(isd is null ? "not applied" : $"applied, adapted luminance {SceneWeather.AdaptedLuminance:0.###}; bloom and eye adaptation not modelled; sky scale on the sky gradient only")}): {ist}");
            }
        }
        var meshes = new List<GlMesh>();
        var borrowedNpcMeshes = new List<GlMesh>();
        using var shotResources = new ShotResourceScope(renderer, meshes, borrowedNpcMeshes, graphicsShot,
            removeOwnedFromRenderer: graphicsCell is not null);
        var retainedMissingTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int LoadObservedTexture(string category, string gamePath)
        {
            bool isStatic = category.StartsWith("static-", StringComparison.Ordinal);
            var manager = isStatic && graphicsCell is not null ? graphicsCell.InstallTextures : textures;
            if (graphicsCell is null)
            {
                // Measurement only (G4 RED, Gate B8 review Q2): the same queued GL_OUT_OF_MEMORY on the per-shot path.
                var oomOrdinary = isStatic ? graphicsReuseRed?.BeforeOrdinaryLoad(category, gamePath,
                    cache.ResolveAssetPath(gamePath), manager) : null;
                int ordinary = manager.LoadTexture(gamePath);
                GlAllocationRefusal.Check(category, gamePath, ordinary, manager.WhiteTexture, false, false, null);   // F-G4-1
                if (oomOrdinary is not null) graphicsReuseRed!.AfterOrdinaryLoad(oomOrdinary, ordinary, manager);   // measurement only
                if (graphicsShot is not null)
                    graphicsShot.TextureAssigned(category, gamePath, cache.ResolveAssetPath(gamePath), ordinary, manager.WhiteTexture);
                return ordinary;
            }
            string? diskPath = cache.ResolveAssetPath(gamePath);
            BuiltScene.TextureLease LoadAndCaptureMissing()
            {
                // Measurement only (G4 RED, Gate B8): a real GL_OUT_OF_MEMORY queued before one chosen install
                // upload; inert without a RED session.
                var oomProbe = isStatic ? graphicsReuseRed?.BeforeInstallLoad(category, gamePath, diskPath, manager,
                    graphicsCell!.Resident) : null;
                int loaded = manager.LoadTexture(gamePath);
                // The manager's case-insensitive set is the diagnostic contract. A failed asset
                // request enters it but is not handle-cached, so membership survives repeated
                // calls and static-key replacement. White handle alone is insufficient: GL upload
                // failure can also return (and cache) white without recording a missing asset.
                bool missing = manager.MissingTexturePaths.Any(
                    x => StringComparer.OrdinalIgnoreCase.Equals(x, gamePath));
                if (oomProbe is not null)   // measurement only (G4 RED)
                    graphicsReuseRed!.AfterInstallLoad(oomProbe, loaded, missing, manager, graphicsCell!.Resident);
                // F-G4-1: an allocation failure refuses before the first draw (the install manager is the resident one).
                GlAllocationRefusal.Check(category, gamePath, loaded, manager.WhiteTexture, missing, diskPath is not null,
                    isStatic ? graphicsCell!.Resident : null);
                return new(loaded, missing ? [gamePath] : []);
            }
            var lease = isStatic && builtScene is not null
                ? builtScene.Texture(category, gamePath, diskPath, LoadAndCaptureMissing, manager.WhiteTexture)
                : LoadAndCaptureMissing();
            int handle = lease.Handle;
            retainedMissingTexturePaths.UnionWith(lease.MissingPaths);
            graphicsShot?.TextureAssigned(category, gamePath, diskPath, handle, manager.WhiteTexture);
            return handle;
        }

        int staticWhite = graphicsCell?.InstallTextures.WhiteTexture ?? textures.WhiteTexture;

        // A static shape's diffuse. Under --depth-measurement it is loaded only when the shape is alpha-tested: the
        // alpha test's discard is the one way a texture reaches the depth buffers (DepthMeasurement.cs).
        int MeasuredDiffuse(string category, string gamePath, bool hasAlphaTest)
        {
            if (depthMeasurement is null) return LoadObservedTexture(category, gamePath);
            if (!DepthMeasurement.KeepsDiffuse(hasAlphaTest)) return staticWhite;
            int handle = LoadObservedTexture(category, gamePath);
            depthMeasurement.NoteKept(gamePath, cache.ResolveAssetPath(gamePath));
            return handle;
        }

        // A 1x1 orange diffuse for the highlighted ref — the same signal color the flat mode uses.
        int CreateOrange()
        {
            int made = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, made);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1, 1, 0,
                          PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 255, 90, 26, 255 });
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            return made;
        }
        var orangeLease = builtScene?.OwnedTexture("static-diagnostic-orange", CreateOrange);
        int orange = orangeLease?.Handle ?? (live is not null ? live.Orange(CreateOrange) : CreateOrange());
        if (builtScene is not null) graphicsShot?.TrackCellOwnedTexture("diagnostic-orange", orange);
        else if (live is null) { graphicsShot?.TrackOwnedTexture("diagnostic-orange", orange); shotResources.TrackTexture(orange); }

        var builtCache = new Dictionary<string, List<NifMeshBuilder.BuiltMesh>?>(StringComparer.OrdinalIgnoreCase);

        // A MATO names a MODEL, not a texture, so the covering's diffuse is whatever that model's
        // first drawable shape carries in slot 0 (vanilla snow: landscape/snowmatshader.nif). Built
        // through the same builder and cached per path — there are a handful of distinct materials
        // in any scene, against thousands of refs.
        var matoTexCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int MatoTexture(string? modelPath)
        {
            if (modelPath is not { Length: > 0 }) return staticWhite;
            if (depthMeasurement is not null) return staticWhite;   // a covering shell never writes depth
            if (matoTexCache.TryGetValue(modelPath, out var cached)) return cached;
            int handle = staticWhite;
            var mrel = modelPath.StartsWith("meshes", StringComparison.OrdinalIgnoreCase)
                     ? modelPath : Path.Combine("meshes", modelPath);
            try
            {
                var mabs = resolver.ResolveAssetPath(mrel);
                if (mabs is not null)
                    foreach (var mb in builder.BuildFromFile(mabs))
                        if (mb.TexturePaths.TryGetValue(0, out var t0) && t0 is { Length: > 0 })
                        {
                            int h = LoadObservedTexture("static-covering-diffuse", t0);
                            if (h != staticWhite) { handle = h; break; }
                        }
            }
            catch (Exception ex) { logger.LogError($"MATO model '{mrel}'", ex); }
            matoTexCache[modelPath] = handle;
            return handle;
        }
        // A SINGLE-PASS material has no texture of its own: the engine binds the same global
        // textures from textures\effects for every projected draw, whatever was projected, and
        // its lighting path reads two of them (the coverage noise and the projected diffuse).
        // Loaded once, on first use. A missing noise leaves the material to the shell fallback.
        int projectedNoise = -1, projectedDiffuse = -1;
        (int Noise, int Diffuse) ProjectedTextures()
        {
            if (depthMeasurement is not null) return (0, 0);   // colour only; depth never reads it
            if (projectedNoise < 0)
            {
                int nz = LoadObservedTexture("static-covering-noise", @"textures\effects\projectednoise.dds");
                int df = LoadObservedTexture("static-covering-diffuse", @"textures\effects\projecteddiffuse.dds");
                projectedNoise = nz == staticWhite ? 0 : nz;
                projectedDiffuse = df == staticWhite ? 0 : df;
            }
            return (projectedNoise, projectedDiffuse);
        }
        int shapes = 0, missingMesh = 0, texturedShapes = 0;
        // Effect-shader shapes dropped this render: the total shape-draws, and how many such
        // shapes each distinct mesh carries (a per-file property, so it is SET, not accumulated,
        // however many refs place that mesh).
        int fxShapesSkipped = 0, nmShapesSkipped = 0, fxRefCount = 0;
        int fxStaticShapes = 0, fxStaticRefCount = 0;
        int editorMarkerShapes = 0, emissiveShapes = 0, envMappedShapes = 0;
        int cullTrisDropped = 0, cullShapesDropped = 0;
        int coveringShapes = 0, coveringRefs = 0, projectedShapes = 0;
        var coveringByMato = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int waterMeshes = 0; long waterTris = 0;
        long waterSubmerged = 0, waterSamples = 0;
        var fxByMesh = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // env VP_SHAPE_DUMP=<substr> — the SHAPE-level companion to --list-meshes: for every mesh
        // whose path matches, print what the builder actually read off each shape. --list-meshes
        // says which files are in the cell; this says why one of their shapes draws as it does.
        // Printed once per distinct mesh, at build time, before any skip decision.
        string? shapeDump = Environment.GetEnvironmentVariable("VP_SHAPE_DUMP");
        // env VP_NIF_DUMP=<substr> — the block-level companion to VP_SHAPE_DUMP: SkyRender's full NIF
        // dump (scene graph, every block's fields) for each matching mesh, once, before it is built.
        string? nifDump = Environment.GetEnvironmentVariable("VP_NIF_DUMP");
        var nifDumped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void NifDumpIfAsked(string rel, string abs)
        {
            if (nifDump is not { Length: > 0 } || !rel.Contains(nifDump, StringComparison.OrdinalIgnoreCase) || !nifDumped.Add(rel)) return;
            try { Console.WriteLine($"   ---- NIF dump of {rel} ----"); Console.WriteLine(NifDumpApi.DumpFileToString(abs)); }
            catch (Exception ex) { Console.WriteLine($"   ---- NIF dump of {rel} failed: {ex.Message}"); }
        }
        var shapeDumped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Highlighted refs keep their own texture; their halo (an inflated inverted hull) is
        // emitted here and drawn by the OVERLAY pass, whose zero-normal vertices render as the
        // exact flat colour — the library pass would shade the hull's inward faces to black.
        var haloTris = new List<float>();
        var subjectSurface = new List<float>();
        var subjectSurfaceDoubleSided = new List<float>();
        var subjectSurfaceRefusals = new HashSet<string>();
        // --coverage-of (CaptureViews.cs): each named ref's non-opaque meshes, drawn alone after the frame.
        var coverageMeshes = new Dictionary<string, List<GlMesh>>(StringComparer.Ordinal);
        var coverageKeys = CaptureViews.CoverageOf.Select(c => CaptureViews.CoverageSpec(c).Key).ToHashSet(StringComparer.Ordinal);
        // --protect / --contacts-of (CaptureViews.cs): every drawn ref's world triangles, before any --cull-above.
        var captureGeom = CaptureViews.WantsGeometry ? new CaptureViews.Geometry() : null;
        // --ref-surface: per highlighted ref, its drawn shapes and how many of them are non-opaque (refused)
        var surfaceShapes = new Dictionary<string, (int Total, int Refused)>(StringComparer.Ordinal);
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
        // The meshes the renderer drew nothing for, kept so UnreadableAssets can re-read each one
        // AFTER the render and say which of them was found-and-unreadable rather than absent or
        // simply empty. A count cannot be re-read; these can.
        var noGeometry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Where each of those is placed, so a hole the census names can be put on the map.
        var noGeometryAt = new Dictionary<string, List<UnreadableAssets.Placement>>(StringComparer.OrdinalIgnoreCase);
        // The Base Object Swapper layer's paint: the swapped refs' meshes, tinted in `diff` (BosView.cs).
        var bosView = new BosViewState(bosMode);
        // The world state's paint: the refs the flip brought in and took out (StateView.cs).
        var stateView = new StateViewState(stateMode, stateName);

        ViewProbe.RenderStages.Open(ViewProbe.RenderStages.SceneBuild,
            "resolving and building every reference's meshes and textures", refs.Count, "references");
        foreach (var r in refs)
        {
            ViewProbe.RenderStages.Advance();
            var ro = new RefOcclusion { Ref = r };
            occl.Refs.Add(ro); // Missing and unsupported assets stay in the audit population.
            var rel = r.MeshRelPath.StartsWith("meshes", StringComparison.OrdinalIgnoreCase)
                ? r.MeshRelPath : Path.Combine("meshes", r.MeshRelPath);
            if (!boundsCache.TryGetValue(rel, out var source))
            {
                var boundPath = meshReadHooks is null ? resolver.ResolveAssetPath(rel) : meshReadHooks.Resolve(rel);
                source = (null, "", "model asset missing");
                if (needOcclusion && boundPath is not null)
                {
                    try
                    {
                        var bytes = meshReadHooks?.ReadBytes(boundPath) ?? File.ReadAllBytes(boundPath);
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
            string rendererInputHash = "";
            if (!builtCache.TryGetValue(rel, out var built))
            {
                if (cellScene is null && live is null)
                {
                    built = null;
                    var abs = meshReadHooks is null ? resolver.ResolveAssetPath(rel) : meshReadHooks.Resolve(rel);
                    if (abs is not null)
                    {
                        try
                        {
                            NifDumpIfAsked(rel, abs);
                            built = meshReadHooks is null ? builder.BuildFromFile(abs) : meshReadHooks.Build(abs);
                            StripNifRootTransform(abs, built);
                            if (needOcclusion && source.Hash.Length > 0)
                            {
                                var renderedBytes = meshReadHooks?.ReadBytes(abs) ?? File.ReadAllBytes(abs);
                                rendererInputHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(renderedBytes));
                                if (rendererInputHash != source.Hash)
                                    geometryInputIssues[rel] = "model asset changed between bounds and renderer reads";
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError($"BuildFromFile('{rel}')", ex);
                            geometryInputIssues[rel] = $"renderer geometry read failed: {ex.GetType().Name}: {ex.Message}";
                        }
                    }
                }
                else
                {
                    MeshOutcome outcome;
                    if (!meshState.TryGet(rel, out outcome!))
                    {
                        var abs = meshReadHooks is null ? resolver.ResolveAssetPath(rel) : meshReadHooks.Resolve(rel);
                        if (abs is not null) NifDumpIfAsked(rel, abs);
                        var (read, identityFailure) = ReadOutcome(abs,
                            meshReadHooks is null ? p => builder.BuildFromFile(p) : meshReadHooks.Build,
                            p => meshReadHooks?.ReadBytes(p) ?? File.ReadAllBytes(p));
                        outcome = read;
                        // A build failure is itself the stable outcome to project each shot.
                        // Successful geometry needs a real identity; empty is never equality.
                        if (outcome.MissingPath || outcome.Failure is not null || identityFailure is null) meshState.Store(rel, outcome);
                        if (outcome.Failure is null && identityFailure is not null)
                            geometryInputIssues[rel] = $"renderer identity read failed: {identityFailure.GetType().Name}: {identityFailure.Message}";
                    }
                    built = outcome.Built;
                    rendererInputHash = outcome.InputHash;
                    if (outcome.Failure is { } failure)
                    {
                        logger.LogError($"BuildFromFile('{rel}')", failure);
                        geometryInputIssues[rel] = $"renderer geometry read failed: {failure.GetType().Name}: {failure.Message}";
                    }
                    if (needOcclusion && source.Hash.Length > 0 && rendererInputHash.Length > 0
                        && rendererInputHash != source.Hash)
                        geometryInputIssues[rel] = "model asset changed between bounds and renderer reads";
                }
                builtCache[rel] = built;
            }
            meshReadHooks?.Observe?.Invoke(rel, built,
                geometryInputIssues.TryGetValue(rel, out var observedIssue) ? observedIssue : null,
                source.Issue, source.Hash, rendererInputHash);
            if (geometryInputIssues.TryGetValue(rel, out var geometryInputIssue)) ro.Issue ??= geometryInputIssue;
            if (built is null || built.Count == 0)
            {
                missingMesh++; ro.Issue ??= "renderer produced no geometry"; noGeometry.Add(rel);
                captureGeom?.MarkUnassessed(r.RefKey.ToString(), r.BaseEditorId ?? "", "its mesh drew no geometry",
                    new Vector3(r.Position.X, r.Position.Y, r.Position.Z));
                if (!noGeometryAt.TryGetValue(rel, out var atList)) noGeometryAt[rel] = atList = new();
                atList.Add(new UnreadableAssets.Placement(r.RefKey.ToString(), r.Position, r.Scale));
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
                      + (b.Positions.Length > 0
                          ? $"  box=({b.Positions.Min(v => v.X):0},{b.Positions.Min(v => v.Y):0},{b.Positions.Min(v => v.Z):0})"
                            + $"..({b.Positions.Max(v => v.X):0},{b.Positions.Max(v => v.Y):0},{b.Positions.Max(v => v.Z):0})"
                          : "")
                      + $"  blend={(b.HasAlphaBlend ? $"{b.SrcBlendIndex}->{b.DstBlendIndex}" : "off")}"
                      + $"  matAlpha={b.MaterialAlpha:0.00}"
                      + (b.HasEffectShader
                          ? $"  base=({b.EffectBaseColor.X:0.00},{b.EffectBaseColor.Y:0.00},{b.EffectBaseColor.Z:0.00},a{b.EffectBaseColorAlpha:0.00})x{b.EffectBaseColorScale:0.00}"
                          : $"  emis=({b.EmissiveColor.X:0.00},{b.EmissiveColor.Y:0.00},{b.EmissiveColor.Z:0.00})x{b.EmissiveMultiple:0.00}")
                      + $"  zwrite={b.ZBufferWrite}"
                      + (b.IsDecal ? "  DECAL" : "")
                      + (b.BillboardMode >= 0 ? $"  BILLBOARD{b.BillboardMode}" : "")
                      + $"  sf1=0x{b.ShaderFlags1:X8} sf2=0x{b.ShaderFlags2:X8}"
                      + $"  tex0={(b.TexturePaths.TryGetValue(0, out var t0) && t0.Length > 0 ? t0 : "(none)")}"
                      + (b.HasEffectShader
                          ? $"  fx1=0x{b.EffectShaderFlags1:X8} fx2=0x{b.EffectShaderFlags2:X8}"
                            + $" falloff=({b.EffectFalloff.X:0.00},{b.EffectFalloff.Y:0.00},{b.EffectFalloff.Z:0.00},{b.EffectFalloff.W:0.00})"
                            + $" soft={b.EffectSoftFalloffDepth:0.#} light={b.EffectLightingInfluence:0.00} clamp={b.EffectTextureClampMode}"
                            + $" uv=({b.EffectUvScale.X:0.##},{b.EffectUvScale.Y:0.##})+({b.EffectUvOffset.X:0.##},{b.EffectUvOffset.Y:0.##})"
                            + $" grey={b.EffectGreyscaleTexture ?? "(none)"}"
                          : ""));
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
                // A shape named "EditorMarker" is the editor's stand-in (the Creation Kit draws it, the
                // game hides it). Unseen while effect shapes were dropped wholesale; with them drawn, the
                // fishing-spot markers (an untextured orange effect cone) painted orange wisps over
                // Riverwood's river bank (2026-10-29).
                if (b.ShapeName.StartsWith("EditorMarker", StringComparison.OrdinalIgnoreCase) && !r.Marker)
                { part.Issue ??= "editor-marker shape is not gameplay render geometry"; editorMarkerShapes++; continue; }

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
                // Effect-shader shapes draw through SkyRender's effect shading whenever they are drawn
                // at all (default, --fx-static, --fx); --no-fx clears fxStatic and drops them again.
                // Depth measurement keeps its old exclusion: effects are not surfaces.
                bool drawStatic = (fxStatic || keepFx) && b.HasEffectShader && !normalAsDiffuse
                                  && depthMeasurement is null;
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
                captureGeom?.Add(r.RefKey.ToString(), r.BaseEditorId ?? "", wp, b.Indices);
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

                var meshVertices = Interleave(wp, wn, b.TextureCoordinates, wt, wb, b.VertexColors);
                var retainedMesh = builtScene?.AcquireMesh("static-ref", meshVertices, tri);
                var mesh = retainedMesh?.Mesh ?? new GlMesh();
                bool meshCreatedNow = retainedMesh?.CreatedNow ?? true;
                string? meshUploadHash = retainedMesh?.InputSha256;
                if (graphicsShot is not null && meshUploadHash is null)
                    meshUploadHash = BuiltScene.HashArrays(meshVertices, tri);
                if (meshCreatedNow)
                {
                    mesh.Upload(meshVertices, tri);
                    graphicsShot?.MeshUploaded("static-ref", mesh, meshVertices, tri);
                }
                mesh.ShapeName = b.ShapeName;
                mesh.HasVertexColors = b.VertexColors is { Length: > 0 };
                // SLSF2_Tree_Anim: vertex alpha is the wind-sway weight, not opacity. Reading it as
                // opacity cut most leaf cards away under the alpha test (trees drew as bare trunks).
                mesh.VertexAlphaIsWindWeight = (b.ShaderFlags2 & (1u << 29)) != 0;
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
                    mesh.DiffuseTexture = hd is { Length: > 0 } ? MeasuredDiffuse("static-ref-diffuse", hd, b.HasAlphaTest) : staticWhite;
                    if (mesh.DiffuseTexture != staticWhite) texturedShapes++;

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
                    mesh.DiffuseTexture = md is { Length: > 0 } ? MeasuredDiffuse("static-ref-marker", md, b.HasAlphaTest) : orange;
                    mesh.HasAlphaBlend = true;
                    mesh.MaterialAlpha = 0.5f;
                    mesh.DepthWrite = false;
                }
                else
                {
                    string? diffuse = b.TexturePaths.TryGetValue(0, out var d) ? d : null;
                    string? normal = b.TexturePaths.TryGetValue(1, out var nrm) ? nrm : null;
                    mesh.DiffuseTexture = diffuse is { Length: > 0 } ? MeasuredDiffuse("static-ref-diffuse", diffuse, b.HasAlphaTest) : staticWhite;
                    if (mesh.DiffuseTexture != staticWhite) texturedShapes++;
                    if (normal is { Length: > 0 } && depthMeasurement is null)   // lighting only; never depth
                    {
                        int h = LoadObservedTexture("static-ref-normal", normal);
                        if (h != staticWhite) { mesh.NormalTexture = h; mesh.HasNormalMap = true; }
                    }
                }

                if (coverageKeys.Contains(r.RefKey.ToString()) &&
                    (b.HasAlphaBlend || b.HasAlphaTest || b.MaterialAlpha < 1f || !b.ZBufferWrite || b.HasEffectShader))
                {
                    if (!coverageMeshes.TryGetValue(r.RefKey.ToString(), out var cml)) coverageMeshes[r.RefKey.ToString()] = cml = new();
                    cml.Add(mesh);
                }

                if (r.Highlight && refSurface)
                {
                    // An identity pass must preserve the scene depth and the normal material path.
                    // A solid overlay cannot reproduce alpha-card coverage, so these participants
                    // remain explicitly unqualified instead of becoming opaque diagnostic sheets.
                    bool refusedShape = b.HasAlphaBlend || b.HasAlphaTest || b.MaterialAlpha < 1f ||
                        !b.ZBufferWrite || b.HasEffectShader || r.Marker;
                    var sk = r.RefKey.ToString();
                    surfaceShapes[sk] = surfaceShapes.TryGetValue(sk, out var ss) ? (ss.Total + 1, ss.Refused + (refusedShape ? 1 : 0))
                                                                                   : (1, refusedShape ? 1 : 0);
                    if (refusedShape)
                        subjectSurfaceRefusals.Add(sk);
                    else
                    {
                        var surface = b.IsDoubleSided ? subjectSurfaceDoubleSided : subjectSurface;
                        var colour = r.HighlightColor == default ? haloOrange : r.HighlightColor;
                        foreach (var index in tri) Overlay.Vertex(surface, wp[index], colour, Vector3.Zero);
                    }
                }

                // ---- EMISSIVE AND GLOW MAP (lighting shapes) ----------------------------------
                // SLSF1_Own_Emit adds emissiveColor x emissiveMultiple (x the slot-2 glow map under
                // SLSF2_Glow_Map on non-skin shader types), absorbed by the albedo, as SkyRender's
                // NifMaterialBinder does. Candle wax, chandelier chains, enchanted gear. Skipped when
                // SLSF1_External_Emittance is set: the engine then takes the colour from outside the
                // NIF (interior windows: the cell's window light), which this tool does not model, so
                // those keep drawing unlit as before. Set on every bind: a retained mesh keeps whatever
                // the last bind wrote. Never under --depth-measurement (lighting only).
                mesh.HasEmissive = false;
                mesh.HasGlowMap = false;
                mesh.GlowTexture = 0;
                bool ownEmit = (b.ShaderFlags1 & (1u << 22)) != 0 && (b.ShaderFlags1 & (1u << 29)) == 0;
                if (ownEmit && !b.HasEffectShader && depthMeasurement is null && !r.Marker)
                {
                    mesh.HasEmissive = true;
                    mesh.EmissiveColor = b.EmissiveColor;
                    mesh.EmissiveMultiple = b.EmissiveMultiple;
                    bool skin = b.ShaderType == 4 || b.ShaderType == 5;
                    if ((b.ShaderFlags2 & (1u << 6)) != 0 && !skin && b.TexturePaths.TryGetValue(2, out var glowPath)
                        && glowPath.Length > 0)
                    {
                        int g = LoadObservedTexture("static-ref-glow", glowPath);
                        if (g != staticWhite) { mesh.GlowTexture = g; mesh.HasGlowMap = true; }
                    }
                    emissiveShapes++;
                }

                // ---- ENVIRONMENT MAP (lighting shapes) -----------------------------------------
                // SLSF1_Environment_Mapping (bit 7) or SLSF1_Eye_Environment_Mapping (bit 17) with a
                // slot-4 texture: the cube map (or the 2D fallback) and the slot-5 mask, with the
                // NIF's scales, as SkyRender's NifMaterialBinder does for characters. Metal fittings,
                // glass, ice, polished stone. Set on every bind (a retained mesh keeps whatever the
                // last bind wrote). The handles live in the texture manager's own cache, not in a
                // scene lease. Never under --depth-measurement. VP_NO_ENVMAP=1 leaves them off.
                mesh.HasEnvironmentMap = false;
                mesh.HasEnvMask = false;
                mesh.IsEnvMap2D = false;
                mesh.EnvMapTexture = 0;
                mesh.EnvMaskTexture = 0;
                bool envFlag = (b.ShaderFlags1 & ((1u << 7) | (1u << 17))) != 0;
                if (envFlag && !b.HasEffectShader && depthMeasurement is null && !r.Marker
                    && b.TexturePaths.TryGetValue(4, out var envPath) && envPath.Length > 0
                    && Environment.GetEnvironmentVariable("VP_NO_ENVMAP") != "1")
                {
                    var envManager = graphicsCell is not null ? graphicsCell.InstallTextures : textures;
                    var (envTex, envIsCube) = envManager.LoadEnvMap(envPath);
                    if (envTex != 0)
                    {
                        mesh.EnvMapTexture = envTex;
                        mesh.HasEnvironmentMap = true;
                        mesh.IsEnvMap2D = !envIsCube;
                        mesh.EnvMapScale = b.EnvironmentMapScale;
                        mesh.EyeCubemapScale = b.EyeCubemapScale;
                        mesh.UseEyeCubemapScale = b.ShaderType == 16;   // BSLSP_EYE
                        if (b.TexturePaths.TryGetValue(5, out var maskPath) && maskPath.Length > 0)
                        {
                            int m = LoadObservedTexture("static-ref-envmask", maskPath);
                            if (m != staticWhite) { mesh.EnvMaskTexture = m; mesh.HasEnvMask = true; }
                        }
                        envMappedShapes++;
                    }
                }

                if (drawStatic)
                {
                    // ENGINE EFFECT SHADING (SkyRender's effect path, frozen at the authored frame:
                    // the BSEffectShaderPropertyFloatControllers that animate UV/alpha are never
                    // run, so the block's stored values ARE the frame). Source texture x baseColor,
                    // the greyscale palette and baseColorScale, view-angle falloff, the material's
                    // own double-sided / depth flags, and the NiAlphaProperty blend (6->0 additive
                    // for flames). Replaces the 2026-08-28 emissive stand-in, which drew
                    // albedo x (lighting + emissive) and needed VP_FXSTATIC_EMIT to look like fire.
                    string? grey = b.EffectGreyscaleTexture;
                    int greyTex = grey is { Length: > 0 } ? LoadObservedTexture("static-ref-effect-greyscale", grey) : 0;
                    if (greyTex == staticWhite) greyTex = 0;
                    NifMaterialBinder.ApplyEffectMaterial(mesh, b, mesh.DiffuseTexture,
                        mesh.DiffuseTexture != staticWhite, greyTex);
                    // Billboards: the builder's frame is in NIF root space, but this mesh's vertices
                    // are already placed in the world, so the frame takes the same placement (each
                    // axis row rotated and scaled, the origin placed). SkyRender then turns the shape
                    // about that origin toward the camera each draw. VP_NO_BILLBOARD=1 keeps the
                    // authored pose.
                    if (mesh.BillboardFrame is { } bfr && Environment.GetEnvironmentVariable("VP_NO_BILLBOARD") != "1")
                    {
                        GlVec3 Axis(GlVec3 a)
                        {
                            var w = YUp(Vector3.TransformNormal(new Vector3(a.X, -a.Z, a.Y) * r.Scale, rot));
                            return new GlVec3(w.X, w.Y, w.Z);
                        }
                        var bo = bfr.Row3.Xyz;
                        var wo = YUp(Vector3.Transform(new Vector3(bo.X, -bo.Z, bo.Y) * r.Scale, rot) + pos);
                        mesh.BillboardFrame = new OpenTK.Mathematics.Matrix4(
                            new GlVec4(Axis(bfr.Row0.Xyz), 0f), new GlVec4(Axis(bfr.Row1.Xyz), 0f),
                            new GlVec4(Axis(bfr.Row2.Xyz), 0f), new GlVec4(wo.X, wo.Y, wo.Z, 1f));
                    }
                    else mesh.BillboardFrame = null;
                    // env VP_FX_DOUBLESIDED=1 — diagnostic: draw every effect shape double-sided, to
                    // tell a back-face-culled quad from one the shading itself hides.
                    if (Environment.GetEnvironmentVariable("VP_FX_DOUBLESIDED") == "1") mesh.IsDoubleSided = true;
                    if (shapeDump is { Length: > 0 } && rel.Contains(shapeDump, StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"      effect drawn: {b.ShapeName} ref {r.RefKey} render={mesh.ShouldRender} blend={mesh.HasAlphaBlend} "
                                        + $"test={mesh.UseAlphaTest}@{mesh.AlphaThreshold:0.00} tris={tri.Length / 3}"
                                        + (mesh.BillboardFrame is { } dbf ? $" billboard {mesh.BillboardMode} frame X{dbf.Row0.Xyz:0.00} Y{dbf.Row1.Xyz:0.00} Z{dbf.Row2.Xyz:0.00} o{dbf.Row3.Xyz:0.0}" : ""));
                    fxStaticShapes++;
                    refHadFxStatic = true;
                }
                else mesh.IsEffectShader = false;   // a retained mesh keeps whatever the last bind wrote

                // ---- SINGLE-PASS COVERING (MATO) -------------------------------------------
                // The engine draws a single-pass material inside the base shape's OWN lighting
                // shader (Projected_UV), per pixel against the normal-mapped normal, so the snow
                // settles into the rock's relief with a noise-broken edge. SkyRender's projected
                // material is that path. The direction is world up: the single-pass path reads
                // only scale, bias, noise scale, colour and the STAT's max angle, never the
                // MATO's projection vector. Set on every bind and cleared when absent, because a
                // retained mesh keeps whatever the last bind wrote.
                mesh.HasProjectedMaterial = false;
                bool projectedDrawn = false;
                if (cover is { SinglePass: true } spc && ProjectedTextures() is { Noise: > 0 } ptx)
                {
                    mesh.HasProjectedMaterial = true;
                    mesh.ProjectedDirection = Vector3.UnitY;
                    // MAX ANGLE. The reverse-engineered account SkyRender cites folds the STAT's
                    // max angle into the threshold (cos + (1 - cos) * ...). Against the game it
                    // under-covers: the 2026-10-29 close-up of MountainCliff03_LightSN (max angle
                    // 70) shows snow on faces that fold admits nowhere near, while dropping the
                    // fold (equivalently, angle 90) matches by eye. So 90 by default, judged
                    // in game rather than settled by a source; VP_PROJ_MAXANGLE=stat restores the
                    // fold for the next comparison.
                    float projAngle = Environment.GetEnvironmentVariable("VP_PROJ_MAXANGLE") == "stat" ? spc.MaxAngleDeg : 90f;
                    mesh.ProjectedParams = GlMesh.ProjectedParamsFor(spc.FalloffScale, spc.FalloffBias,
                                                                     spc.NoiseUvScale, projAngle);
                    mesh.ProjectedColor = new Vector3(spc.Color.X, spc.Color.Y, spc.Color.Z);
                    mesh.ProjectedDiffuseTiling = spc.DiffuseTiling;
                    mesh.ProjectedNoiseTexture = ptx.Noise;
                    mesh.ProjectedDiffuseTexture = spc.ProjectedDiffuse ? ptx.Diffuse : 0;
                    projectedDrawn = true;
                    projectedShapes++;
                    refHadCovering = true;
                    coveringByMato[spc.Label] = coveringByMato.TryGetValue(spc.Label, out var pcn) ? pcn + 1 : 1;
                }

                if (meshCreatedNow)
                {
                    renderer.AddMesh(mesh);
                    builtScene?.AddedToRenderer(mesh);
                    graphicsShot?.MeshAdded("static-ref", mesh);
                }
                if (graphicsShot is not null) graphicsShot.StaticBound("static-ref", mesh, meshUploadHash!);
                if (builtScene is null) meshes.Add(mesh);
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

                // ---- PROJECTED COVERING SHELL (multi-pass MATO) ---------------------------------
                // A MULTI-PASS material builds a second piece of geometry in the engine; a
                // single-pass one is drawn above, inside the base shader, and lands here only
                // when the global projected textures could not be loaded.
                // A second shell over the shape just drawn, sharing its triangles: same verts,
                // per-vertex coverage in the ALPHA channel, UVs planar-projected along the
                // material's own projection vector. Emitted per SHAPE rather than per ref because
                // coverage is a function of each face's normal — one mesh can have a snowed roof
                // and bare undersides, and that difference IS the effect.
                if (cover is { } cv && !projectedDrawn)
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
                        var coveringVertices = Interleave(cp, wn, cu, wt, wb, cc);
                        var retainedCovering = builtScene?.AcquireMesh("static-covering", coveringVertices, tri);
                        var covMesh = retainedCovering?.Mesh ?? new GlMesh();
                        bool coveringCreatedNow = retainedCovering?.CreatedNow ?? true;
                        string? coveringUploadHash = retainedCovering?.InputSha256;
                        if (graphicsShot is not null && coveringUploadHash is null)
                            coveringUploadHash = BuiltScene.HashArrays(coveringVertices, tri);
                        if (coveringCreatedNow)
                        {
                            covMesh.Upload(coveringVertices, tri);
                            graphicsShot?.MeshUploaded("static-covering", covMesh, coveringVertices, tri);
                        }
                        covMesh.ShapeName = $"covering:{cv.Label}";
                        covMesh.HasVertexColors = true;      // the shader's baseColor.a *= vertexColor.a
                        covMesh.HasAlphaBlend = true;
                        covMesh.SrcBlendIndex = 6;           // SRC_ALPHA
                        covMesh.DstBlendIndex = 7;           // INV_SRC_ALPHA — plain "over"
                        covMesh.DepthWrite = false;
                        covMesh.IsDecal = true;              // drawn after the sorted blend set, never writes depth
                        covMesh.IsDoubleSided = mesh.IsDoubleSided;
                        covMesh.DiffuseTexture = MatoTexture(cv.ModelPath);
                        if (coveringCreatedNow)
                        {
                            renderer.AddMesh(covMesh);
                            builtScene?.AddedToRenderer(covMesh);
                            graphicsShot?.MeshAdded("static-covering", covMesh);
                        }
                        if (graphicsShot is not null) graphicsShot.StaticBound("static-covering", covMesh, coveringUploadHash!);
                        if (builtScene is null) meshes.Add(covMesh);
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

        // --hide / --contacts-of / --protect (CaptureViews.cs). Hidden refs keep their meshes (the retained scene and the
        // CPU placement queries are untouched) and are switched off for every draw of this shot; ApplyHide runs again
        // before the frame, after anything that may have switched meshes back on.
        var hiddenPresent = new List<string>();
        void ApplyHide()
        {
            foreach (var key in CaptureViews.Hide)
                foreach (var o in occl.Refs)
                    if (o.Ref.RefKey.ToString() == key)
                        foreach (var m in o.Meshes) m.IsRendering = false;
        }
        foreach (var key in CaptureViews.Hide)
        {
            var hro = occl.Refs.Where(o => o.Ref.RefKey.ToString() == key && o.Meshes.Count > 0).ToList();
            if (hro.Count == 0)
            {
                Console.WriteLine($"hidden: {key} not drawn in this setup (not placed, disabled, or no geometry); nothing to hide");
                continue;
            }
            hiddenPresent.Add(key);
            Console.WriteLine($"hidden: {key} ({hro[0].Ref.BaseEditorId ?? "?"}, {hro.Sum(o => o.Meshes.Count)} shape(s)) is not drawn in this frame");
        }
        ApplyHide();
        if (captureGeom is not null)
        {
            foreach (var key in CaptureViews.ContactsOf)
            {
                var ans = CaptureViews.Contacts(captureGeom, key);
                if (ans.Refusal == "absent") { Console.WriteLine($"contacts-of {key}: not drawn in this setup"); continue; }
                if (ans.Refusal is not null) { Console.WriteLine($"contacts-of {key}: undecided: {ans.Refusal}"); continue; }
                Console.WriteLine(CaptureViews.Line(ans));
                Console.WriteLine(CaptureViews.BoxLine(ans));
            }
            foreach (var key in CaptureViews.Protect)
            {
                var ans = CaptureViews.Contacts(captureGeom, key);
                if (ans.Refusal == "absent")
                {
                    Console.WriteLine($"protect: {key} is not drawn in this setup; nothing of it to protect");
                    continue;
                }
                if (ans.Refusal is not null && hiddenPresent.Count > 0)
                    throw new CaptureRefusal($"protect refused: {ans.Refusal}; nothing may be hidden around it");
                foreach (var h in hiddenPresent)
                {
                    if (ans.Touches.Contains(h))
                        throw new CaptureRefusal($"protect refused: {h} touches {key} (its render surfaces within {CaptureViews.TouchTolerance:0} u), so hiding it would erase the clash; hide something else or take the top-down view");
                    if (ans.RestsOn.Contains(h))
                        throw new CaptureRefusal($"protect refused: {key} rests on {h}, so hiding it would erase the clash; hide something else or take the top-down view");
                    if (ans.Unassessed.Contains(h))
                        throw new CaptureRefusal($"protect refused: whether {h} touches {key} cannot be decided (its geometry is incomplete), so it may not be hidden");
                }
                Console.WriteLine($"protect: {key} kept: none of the {hiddenPresent.Count} hidden object(s) touches it or holds it up; "
                                + CaptureViews.Line(ans)[("contacts-of " + key + ": ").Length..]);
            }
        }

        // TERRAIN — drawn by SkyRender natively (SkyRender.World): one mesh per cell quadrant, its
        // painted layers (roads, paths, every ground blend) blended in the shader over the quadrant's
        // winning base texture (LTEX -> TXST). Opaque, depth-written, shadowed like any static.
        // Two env toggles for answering "is that wash really this texture, or is something wrong":
        // VP_LAYER_ONLY=<substr> keeps only layers whose diffuse path matches, and
        // VP_LAYER_SOLID=1 ignores the opacity map so the texture is drawn at full strength.
        // Together they isolate one painted layer and show it as itself.
        int terrainTexturedPatches = 0, terrainFallbackPatches = 0;
        string? layerOnly = Environment.GetEnvironmentVariable("VP_LAYER_ONLY");
        bool layerSolid = Environment.GetEnvironmentVariable("VP_LAYER_SOLID") == "1";
        int layerMeshes = 0;
        // The depth-measurement mode reads depth only: painted layers do not change it.
        if (depthMeasurement is not null && terrainLayers.Count > 0)
            Console.WriteLine($"   terrain paint: {terrainLayers.Count} layer(s) not drawn (--depth-measurement)");
        if (terrainPatches.Count > 0)
        {
            // The cells are rebuilt from the walk's terrain sheets (NativeTerrain), so the fresh and
            // the retained walk feed it alike; textures load through the observed categories. Blend
            // and tiling are SkyRender's defaults, matched against in-game captures.
            var buildOptions = new TerrainBuildOptions();
            var blendMode = buildOptions.Blend;
            int? LoadNormal(string? path) => path is { Length: > 0 } && depthMeasurement is null
                && LoadObservedTexture("static-terrain-normal", path) is var nh && nh != staticWhite ? nh : null;
            foreach (var cell in NativeTerrain.FromSheets(terrainPatches, terrainLayers, terrainExtras))
                for (int q = 0; q < 4; q++)
                {
                    var quad = cell.Quadrants[q]!;
                    string basePath = quad.Base?.DiffusePath ?? "";
                    int baseHandle = basePath.Length > 0 && depthMeasurement is null
                        ? LoadObservedTexture("static-terrain-diffuse", basePath) : staticWhite;
                    int? baseNormal = LoadNormal(quad.Base?.NormalPath);
                    if (basePath.Length > 0 && baseHandle != staticWhite) terrainTexturedPatches++; else terrainFallbackPatches++;

                    var kept = new List<TerrainLayer>();
                    var keptHandles = new List<int>();
                    var keptNormals = new List<int?>();
                    if (depthMeasurement is null)
                        foreach (var layer in quad.Layers)
                        {
                            if (kept.Count == TerrainQuadrant.MaxLayers) break;
                            string lp = layer.Texture.DiffusePath;
                            if (layerOnly is { Length: > 0 } && !lp.Contains(layerOnly, StringComparison.OrdinalIgnoreCase)) continue;
                            int lh = LoadObservedTexture("static-terrain-layer", lp);
                            if (lh == staticWhite) continue;   // unresolvable: a white layer is worse than none
                            kept.Add(layerSolid ? layer with { Opacity = layer.Opacity.Select(o => o > 0f ? 1f : 0f).ToArray() } : layer);
                            keptHandles.Add(lh);
                            keptNormals.Add(LoadNormal(layer.Texture.NormalPath));
                        }
                    layerMeshes += kept.Count;

                    var data = WorldMeshBuilder.BuildTerrainQuadrant(cell, (TerrainQuadrantIndex)q, kept, buildOptions);
                    var retained = builtScene?.AcquireMesh("static-terrain", data.Vertices, data.Indices);
                    var mesh = retained?.Mesh ?? new GlMesh();
                    bool createdNow = retained?.CreatedNow ?? true;
                    string? uploadHash = retained?.InputSha256;
                    if (graphicsShot is not null && uploadHash is null)
                        uploadHash = BuiltScene.HashArrays(data.Vertices, data.Indices);
                    if (createdNow)
                    {
                        GlWorldFactory.UploadTerrain(mesh, data);
                        graphicsShot?.MeshUploaded("static-terrain", mesh, data.Vertices, data.Indices);
                    }
                    GlWorldFactory.ApplyTerrainMaterial(mesh, baseHandle, keptHandles, blendMode, baseNormal, keptNormals);
                    if (createdNow)
                    {
                        renderer.AddMesh(mesh);
                        builtScene?.AddedToRenderer(mesh);
                        graphicsShot?.MeshAdded("static-terrain", mesh);
                    }
                    if (graphicsShot is not null) graphicsShot.StaticBound("static-terrain", mesh, uploadHash!);
                    if (builtScene is null) meshes.Add(mesh);
                    if (interactive is not null)
                    {
                        var ps = new PickShape { Kind = PickKind.Terrain, Verts = data.Positions, Tris = data.Indices, TerrainTex = basePath };
                        ps.ComputeBounds();
                        interactive.PickShapes.Add(ps);
                        interactive.AddLayerMesh("terrain", mesh);
                    }
                }
            if (layerMeshes > 0) Console.WriteLine($"   terrain paint: {layerMeshes} painted layer(s) blended natively over their quadrants");
        }

        if (terrainTexturedPatches + terrainFallbackPatches > 0)
            Console.WriteLine($"   terrain paint: {terrainTexturedPatches} textured patch(es), {terrainFallbackPatches} untextured (snow fallback)");

        // Static refs, coverings and terrain have completed their real uploads/AddMesh calls.
        graphicsCell?.CompleteStaticBindings(graphicsShot);
        graphicsShot?.Checkpoint("after-static");

        // ---- WATER --------------------------------------------------------------------------
        // One grid per loaded cell that has water, at that cell's own surface height. A GRID and
        // not a quad for one reason that earns its cost: the shallow-to-deep colour is a function
        // of the real distance down to the LAND underneath, so a shoreline needs vertices along it
        // to shade against. A single quad would paint the whole sea one flat colour and lose the
        // one cue that makes water read as water from above.
        //
        // Built and drawn by SkyRender (WorldMeshBuilder.BuildWater): opaque by default, as the engine
        // draws water, so terrain and statics above the surface hide it without clipping the grid to
        // the coast. The Fresnel sky mix follows the live camera.
        foreach (var wpl in waterPlanes)
        {
            const int WaterGrid = 48;                 // 4096/48 ~ 85 u per quad — fine enough for a shoreline
            // Depth over which shallow fades to deep AND over which the surface closes up to
            // opaque. ~2.7 m of Skyrim: wading depth. Was 700 (≈10 m), which kept almost every
            // real body of water permanently in the "shallow" half of both ramps and is why the
            // sea read as tinted glass.
            const float ShallowBand = 192f;
            bool waterLegacy = Environment.GetEnvironmentVariable("VP_WATER_LEGACY") == "1";
            // The alpha at zero depth. 1 = opaque everywhere, matching the engine's opaque draw.
            float shoreAlpha = float.TryParse(Environment.GetEnvironmentVariable("VP_WATER_SHALLOW_ALPHA"),
                                              out var sa) ? Math.Clamp(sa, 0f, 1f) : 1f;
            int texHandle = wpl.NoiseTexture is { Length: > 0 } wtx && depthMeasurement is null
                ? LoadObservedTexture("dynamic-water-normal", wtx) : 0;   // a normal map; never depth
            bool hasNoise = texHandle != 0 && texHandle != textures.WhiteTexture;

            var wmat = new WaterMaterial(wpl.ShallowColor, wpl.DeepColor, wpl.ReflectionColor, wpl.Reflectivity, wpl.Fresnel)
            {
                NoiseTexturePath = wpl.NoiseTexture, NoiseUvScale = wpl.NoiseUvScale,
                ShallowBand = ShallowBand, ShoreAlpha = shoreAlpha, GridResolution = WaterGrid,
                // The engine's water terms (SkyRender RENDERING_PIPELINE.md, "Water"): the reflection is
                // the sky (water reflections are on in the game's default settings), blended by Fresnel
                // near the camera and as a mirror beyond the WATR's noise falloff; the WATR's
                // above-water fog sets the body colour and how far the ground shows through.
                // VP_WATER_LEGACY=1 draws the earlier opaque ShallowBand water for comparison.
                ReflectsSky = !waterLegacy,
                NoiseFalloff = waterLegacy ? 0f : wpl.NoiseFalloff,
                DepthFog = waterLegacy || wpl.FogFar <= 0f ? null : new WaterDepthFog(wpl.FogNear, wpl.FogFar, wpl.FogAmount),
                // Per pixel (the default): the water depth along each pixel's view ray against the
                // opaque scene, the engine's refraction weight and far-distance mirror, and the WATR's
                // sun specular. VP_WATER_VERTEX=1 keeps the earlier per-vertex vertical-depth path.
                PerPixelDepth = !waterLegacy && Environment.GetEnvironmentVariable("VP_WATER_VERTEX") != "1",
                SunSpecularPower = wpl.Spec.X, SunSpecularMagnitude = wpl.Spec.Y, DepthSpecularLighting = wpl.Spec.Z,
            };
            var wdata = WorldMeshBuilder.BuildWater(
                new WaterSurface(wpl.Z, wpl.X0, wpl.Y0, wpl.X1, wpl.Y1, wmat) { Label = wpl.Label }, terrainHeightAt);
            var nwater = new GlMesh();
            GlWorldFactory.UploadWater(nwater, wdata);
            graphicsShot?.MeshUploaded("dynamic-water", nwater, wdata.Vertices, wdata.Indices);
            GlWorldFactory.ApplyWaterMaterial(nwater, wmat, textures.WhiteTexture, hasNoise ? texHandle : null);
            renderer.AddMesh(nwater);
            graphicsShot?.MeshAdded("dynamic-water", nwater);
            meshes.Add(nwater);
            interactive?.AddLayerMesh("water", nwater);
            waterMeshes++; waterTris += wdata.Indices.Length / 3;
            waterSubmerged += wdata.SubmergedVertexCount; waterSamples += wdata.Positions.Length;
        }
        if (waterMeshes > 0)
            Console.WriteLine($"   water: {waterMeshes} cell surface(s), {waterTris:N0} triangles — "
                            + $"{waterPlanes[0].Label} at z {waterPlanes[0].Z:0}"
                            + (waterPlanes.Any(w => w.NoiseTexture is { Length: > 0 })
                                ? ", noise layer bound as a wave normal map" : ", no noise texture resolved")
                            + (Environment.GetEnvironmentVariable("VP_WATER_VERTEX") == "1"
                                ? " (sky reflection, Fresnel and WATR depth fog per vertex; no reflection/refraction buffers or flow — see README)"
                                : " (sky reflection, Fresnel, sun specular, WATR depth fog per pixel along the view ray; no reflection/refraction buffers or flow — see README)"));
        foreach (var wk in waterPlanes.GroupBy(w => w.Label).Select(g => g.First()))
            Console.WriteLine($"      {wk.Label}: shallow {wk.ShallowColor:0.000} deep {wk.DeepColor:0.000} "
                            + $"reflection {wk.ReflectionColor:0.000} reflectivity {wk.Reflectivity:0.###} "
                            + $"fresnel {wk.Fresnel:0.###} opacity {wk.Opacity:0.###} "
                            + $"fog near {wk.FogNear:0.#} far {wk.FogFar:0.#} amount {wk.FogAmount:0.###} noise falloff {wk.NoiseFalloff:0.#} "
                            + $"sun specular power {wk.Spec.X:0.##} magnitude {wk.Spec.Y:0.###} depth specular {wk.Spec.Z:0.###}");
        // HOW MUCH of the water footprint is actually below the surface. The number that tells a
        // wrong HEIGHT from an honest absence: a sea whose floor is mostly above the surface is a
        // height bug, while a low percentage over a mountain cell is just a mountain. Without it,
        // "9 cell surface(s) drawn" reads as success whether or not one pixel of water is visible.
        if (waterSamples > 0)
            Console.WriteLine($"      {100.0 * waterSubmerged / waterSamples:0.0}% of the water footprint has LAND "
                            + $"BELOW the surface (the rest is dry ground, and correctly hides it)");

        // ---- GRASS (default on, --no-grass; GrassLayer.cs) -------------------------------------
        // The GRAS the drawn cells' landscape textures list, placed by SkyRender.World's GrassPlacer on the
        // same ground the terrain draws and drawn instanced: one mesh per grass type and model shape. The
        // models load through the same NIF builder and resolver as every static, in their own space (the
        // instances place them). Cells beyond the grass fade from the eye are skipped (they would draw
        // nothing), except in the fly-through, which can move the eye.
        if (GrassLayer.Requested && depthMeasurement is null)
        {
            var grassSw = System.Diagnostics.Stopwatch.StartNew();
            if (GrassLayer.Cells.Count == 0)
            {
                if (GrassLayer.NoCellsWhy is { } gw) Console.WriteLine($"   grass: not drawn - {gw}");
            }
            else
            {
                var gset = GrassLayer.Settings(out var gsrc);
                var placement = GrassPlacer.Place(GrassLayer.Cells, gset,
                    c => GrassLayer.WaterHeights.TryGetValue((c.GridX, c.GridY), out var wh) ? wh : null,
                    interactive is null ? cam.Eye : null);
                double placeMs = grassSw.Elapsed.TotalMilliseconds;
                int grassShapes = 0, grassNoTexture = 0;
                var glGrass = GlWorldFactory.CreateGrass(placement, type =>
                {
                    string? abs = resolver.ResolveAssetPath(type.ModelPath);
                    if (abs is null) return null;
                    List<NifMeshBuilder.BuiltMesh> built;
                    try { built = builder.BuildFromFile(abs).ToList(); }
                    catch (Exception ex) { logger.LogError($"grass model '{type.ModelPath}'", ex); return null; }
                    var shapes = new List<GlMesh>();
                    foreach (var b in built)
                    {
                        if (b.Positions.Length == 0 || b.Indices.Length == 0 || b.HasEffectShader) continue;
                        var gm = new GlMesh();
                        gm.Upload(Interleave(b.Positions, b.Normals, b.TextureCoordinates, b.Tangents, b.Bitangents, b.VertexColors), b.Indices);
                        gm.ShapeName = $"grass:{Path.GetFileNameWithoutExtension(type.ModelPath)}:{b.ShapeName}";
                        gm.HasVertexColors = b.VertexColors is { Length: > 0 };
                        gm.IsDoubleSided = b.IsDoubleSided;
                        int dh = b.TexturePaths.TryGetValue(0, out var gt0) && gt0 is { Length: > 0 }
                            ? LoadObservedTexture("dynamic-grass", gt0) : textures.WhiteTexture;
                        if (dh == textures.WhiteTexture) grassNoTexture++;
                        gm.DiffuseTexture = dh;
                        // The NIF's own alpha-test threshold; 128 (the usual cutout reference) when it has none.
                        gm.AlphaThreshold = b.HasAlphaTest ? b.AlphaThreshold : 128f / 255f;
                        if (Environment.GetEnvironmentVariable("VP_GRASS_SHAPES") == "1")
                        {
                            var vc = b.VertexColors;
                            string vcs = vc is { Length: > 0 }
                                ? $"vc mean ({vc.Average(c => c.X):0.00},{vc.Average(c => c.Y):0.00},{vc.Average(c => c.Z):0.00},a{vc.Average(c => c.W):0.00})"
                                : "no vc";
                            Console.WriteLine($"     grass shape {gm.ShapeName}: {b.Positions.Length} verts, {b.Indices.Length / 3} tris, {vcs}, "
                                            + $"alpha test {b.HasAlphaTest} {b.AlphaThreshold * 255f:0}, blend {b.HasAlphaBlend}, two-sided {b.IsDoubleSided}, "
                                            + $"sf1=0x{b.ShaderFlags1:X8} sf2=0x{b.ShaderFlags2:X8} type {b.ShaderType}, tex0 {gt0}, "
                                            + $"y {b.Positions.Min(v => v.Y):0}..{b.Positions.Max(v => v.Y):0}");
                        }
                        shapes.Add(gm);
                        grassShapes++;
                    }
                    return shapes;
                });
                foreach (var m in glGrass.Meshes)
                {
                    renderer.AddMesh(m);
                    meshes.Add(m);
                    interactive?.AddLayerMesh("grass", m);
                }
                Console.WriteLine($"   grass: {placement.InstanceCount:N0} instance(s) of {placement.Types.Count(t => t.Instances.Count > 0)} type(s) "
                                + $"({glGrass.Meshes.Count} instanced mesh(es)) on {placement.CellsPlaced} cell(s), {placement.PatchesPlaced:N0} patch(es); "
                                + $"{placement.CellsOutOfReach} cell(s) beyond the fade ({gset.Reach:0} u); settings: {gsrc}"
                                + $"; {GrassLayer.TypesSeen} GRAS in the load order");
                foreach (var t in placement.Types.Where(t => t.Instances.Count > 0).OrderByDescending(t => t.Instances.Count))
                    Console.WriteLine($"     grass {t.Type.ModelPath}: {t.Instances.Count:N0} of {t.Candidates:N0} candidate(s); density {t.Type.Density}%, "
                                    + $"position range {t.Type.PositionRange:0.#}, height range {t.Type.HeightRange:0.##}, colour range {t.Type.ColorRange:0.##}, "
                                    + $"slope {t.Type.MinSlope}-{t.Type.MaxSlope}, flags {t.Type.Flags}, brightness mean {t.Instances.Average(i => i.Shade):0.00}"
                                    + (glGrass.MissingModels.Contains(t.Type) ? " - MODEL NOT FOUND, not drawn" : ""));
                if (grassNoTexture > 0) Console.WriteLine($"   grass: {grassNoTexture} shape(s) without a diffuse (drawn white)");
                Console.WriteLine($"   grass timing: place {placeMs:0} ms, models + textures + upload {grassSw.Elapsed.TotalMilliseconds - placeMs:0} ms");
            }
        }

        // A slice stops here: its references, terrain, water and grass are in the live renderer. What follows is drawn once
        // for the scene's target cell by its first build (LOD, actors, the navmesh, the overlays) or is the one-shot frame.
        if (live is not null)
        {
            liveMeshes?.AddRange(meshes);
            meshes.Clear();                              // the caller owns them now; the shot scope must not free them
            if (interactive is not null) interactive.Occlusion = occl;
            return shapes;
        }
        // ---- DISTANT LOD (--lod, opt-in; DistantLodLayer.cs) --------------------------------
        // Terrain, object and tree LOD beyond the loaded cells, picked for this camera the way the
        // engine picks blocks, and cut away under every cell whose terrain this frame draws (and the
        // walk's loaded set), so the full cells and the LOD never overlap. SkyRender builds and
        // shades it; textures load through the same observed loader as everything else.
        if (DistantLodLayer.Requested)
        {
            var lodSw = System.Diagnostics.Stopwatch.StartNew();
            string? lodWorld = DistantLodLayer.LodWorldspace(out var lodWhy);
            if (lodWorld is null) Console.WriteLine($"   lod: not drawn - {lodWhy}");
            else
            {
                var loadedCells = new HashSet<(int X, int Y)>(DistantLodLayer.WalkCells ?? Array.Empty<(int, int)>());
                foreach (var tris in terrainPatches.Values)
                    foreach (var (a, b, c) in tris)
                        loadedCells.Add(((int)MathF.Floor((a.X + b.X + c.X) / 3f / TerrainCell.CellSize),
                                         (int)MathF.Floor((a.Y + b.Y + c.Y) / 3f / TerrainCell.CellSize)));
                var lodData = DistantLodLoader.Load(new DistantLodRequest
                {
                    Worldspace = lodWorld,
                    Eye = cam.Eye,
                    LoadedCells = new LoadedCells(loadedCells),
                    Kinds = DistantLodLayer.Kinds(),
                    MaxDistance = DistantLodLayer.FarPlane,
                }, p => cache.ResolveAssetPath(p));
                double lodBuildMs = lodSw.Elapsed.TotalMilliseconds;
                if (lodData.Problem is { } lp) Console.WriteLine($"   lod: not drawn - {lp}");
                else
                {
                    int lodTexBefore = textures.MissingTexturePaths.Count;
                    // LOD water: the worldspace's LOD water type through SkyRender's LOD water technique
                    // (opaque, sky reflection, sun specular, fog; no depth or refraction).
                    WaterMaterial? lodWaterMat = DistantLodLayer.LodWaterMaterial(withNoise: depthMeasurement is null);
                    var glLod = GlWorldFactory.CreateDistantLod(lodData, path =>
                    {
                        int h = LoadObservedTexture(path == lodWaterMat?.NoiseTexturePath ? "dynamic-water-normal" : "dynamic-lod", path);
                        return h == textures.WhiteTexture ? null : h;
                    }, textures.WhiteTexture, lodWaterMat);
                    foreach (var m in glLod.Meshes)
                    {
                        renderer.AddMesh(m);
                        meshes.Add(m);
                    }
                    var ls = lodData.Settings!;
                    Console.WriteLine($"   lod: {lodWorld}{lodWhy} - lodsettings SW cell ({ls.SouthWestCellX}, {ls.SouthWestCellY}), stride {ls.Stride}, levels {ls.MinLevel}..{ls.MaxLevel}; "
                                    + $"cut under {loadedCells.Count} loaded cell(s); reach {DistantLodLayer.FarPlane:0} (also the far plane)");
                    foreach (var line in DistantLodLayer.Describe(lodData)) Console.WriteLine(line);
                    foreach (var e in lodData.Errors.Take(5)) Console.WriteLine($"   lod: {e}");
                    if (lodData.Errors.Count > 5) Console.WriteLine($"   lod: ... {lodData.Errors.Count - 5} more file problem(s)");
                    int lodMissing = textures.MissingTexturePaths.Count - lodTexBefore;
                    Console.WriteLine($"   lod timing: read + build {lodBuildMs:0} ms, textures + upload {lodSw.Elapsed.TotalMilliseconds - lodBuildMs:0} ms"
                                    + (lodMissing > 0 ? $"; {lodMissing} LOD texture(s) not found (drawn white)" : ""));
                }
            }
        }

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

        GlMesh BuildFill(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> tris, Vector4 color, string name,
            string graphicsCategory)
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
            var fillIndices = ni.ToArray();
            var fillVertices = Interleave(nv.ToArray(), flat, uv, flat, flat, col);
            mm.Upload(fillVertices, fillIndices);
            graphicsShot?.MeshUploaded(graphicsCategory, mm, fillVertices, fillIndices);
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
        foreach (var actor in npcActors)
            foreach (var am in actor.Meshes)
            {
                renderer.AddMesh(am);
                graphicsShot?.MeshAdded("dynamic-npc-borrowed", am, borrowed: true);
                borrowedNpcMeshes.Add(am);
            }

        // First-person camera: eye/look given in Skyrim coordinates, expressed on the OrbitCamera.
        // Near plane 15 u = the game's `fNearDistance:Display` (read off the running engine 2026-09-05: the
        // compiled-in default, no profile ini sets it; first person uses `fNear1stPersonDistance` 5). It was 4 u
        // before, which drew geometry the game clips in a close-up. That the free camera uses the third-person
        // value is assumed, not measured.
        const float NearPlane = 15f;
        // --lod draws to about 100 cells (DistantLodLayer.FarPlane); without it the far plane is unchanged.
        float FarPlane = DistantLodLayer.Requested ? DistantLodLayer.FarPlane : 131072f;
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

        // --cell-lighting (CellLighting.cs): an interior lit by its own XCLL (with the lighting
        // template's inherited values), its image space and its placed LIGH references.
        if (CellLighting.Requested && CellLighting.TargetCell is { } litCell)
        {
            if (CellLighting.Resolve(litCell) is not { } cl)
                Console.WriteLine("   cell lighting: the cell has no XCLL (or this walk was retained) - studio lights kept");
            else
            {
                var v = cl.Lighting;
                renderer.Lights[0].Color = new GlVec3(v.Ambient.X, v.Ambient.Y, v.Ambient.Z);
                renderer.SetAmbientIntensity(1f);
                renderer.SetKeyLight(azimuthDeg: v.RotXY, elevationDeg: v.RotZ, intensity01: v.DirectionalFade,
                                     new GlVec3(v.Directional.X, v.Directional.Y, v.Directional.Z));
                renderer.SetFillLight(azimuthDeg: 315f, elevationDeg: 20f, intensity01: 0f, new GlVec3(1f, 1f, 1f));
                renderer.Fog = v.FogFar > v.FogNear
                    ? new DistanceFog(new GlVec3(v.FogNearColor.X, v.FogNearColor.Y, v.FogNearColor.Z),
                                      new GlVec3(v.FogFarColor.X, v.FogFarColor.Y, v.FogFarColor.Z),
                                      v.FogNear, v.FogFar, v.FogPower > 0f ? v.FogPower : 1f, v.FogMax > 0f ? v.FogMax : 1f)
                    : null;
                if (cl.ImageSpace is { } cis && !SceneWeather.ImageSpaceOff)
                    renderer.ImageSpace = new ImageSpaceFinish(cis.White, cis.Saturation, cis.Brightness, cis.Contrast,
                        new GlVec3(cis.TintColor.X, cis.TintColor.Y, cis.TintColor.Z), cis.TintAmount, SceneWeather.AdaptedLuminance);
                // Point lights, nearest the eye first, up to the renderer's limit.
                var lit = CellLighting.Placed
                    .Select(p => (P: new Vector3(p.Position.X, p.Position.Y, p.Position.Z), B: CellLighting.Effective(p.Base, p.Ref)))
                    .Where(p => p.B.Radius > 0f)
                    // Diagnostic: VP_CELL_LIGHTS=unshadowed drops the shadow-casting lights (drawn unshadowed here).
                    .Where(p => Environment.GetEnvironmentVariable("VP_CELL_LIGHTS") != "unshadowed" || !p.B.Shadowed)
                    .Where(p => Environment.GetEnvironmentVariable("VP_CELL_LIGHTS") != "none")
                    .OrderBy(p => Vector3.Distance(p.P, cam.Eye))
                    .Take(GlRenderer.MaxPointLights).ToList();
                foreach (var (p, b) in lit)
                {
                    var py = YUp(p);
                    var col = b.Negative ? -b.Color : b.Color;
                    renderer.PointLights.Add(new PointLight(new GlVec3(py.X, py.Y, py.Z), new GlVec3(col.X, col.Y, col.Z), b.Radius) { CastsShadow = b.Shadowed });
                }
                Console.WriteLine($"   cell lighting: {cl.Text}");
                if (Environment.GetEnvironmentVariable("VP_CELL_LIGHTS_LIST") == "1")
                    foreach (var (p, b) in lit)
                        Console.WriteLine($"     light {b.Name} at ({p.X:0}, {p.Y:0}, {p.Z:0}) colour x fade {b.Color:0.###} radius {b.Radius:0}{(b.Negative ? " NEGATIVE" : "")}{(b.Shadowed ? " shadow-casting" : "")}, {Vector3.Distance(p, cam.Eye):0} u from the eye");
                Console.WriteLine($"   cell lighting: {lit.Count} of {CellLighting.Placed.Count} placed light(s) drawn as point lights (nearest first)"
                                  + (lit.Count > 0 ? $"; nearest {lit[0].B.Name} radius {lit[0].B.Radius:0}" : ""));
            }
        }

        // The sun's shadow (--shadows; on by default with --weather): SkyRender's directional shadow
        // map, fitted to the part of the frame within --shadow-distance. Only the sun casts; the
        // ambient is unshadowed, as in the engine.
        if (SceneWeather.ShadowsOn && SceneWeather.Current is not null)
        {
            float groundZ = terrainHeightAt?.Invoke(cam.Eye.X, cam.Eye.Y) ?? MathF.Min(cam.Look.Z, cam.Eye.Z - 100f);
            var (sc, sr) = SceneWeather.ShadowSphere(cam.Eye, cam.Look, cam.FovDeg, sizeW / (float)sizeH, groundZ, SceneWeather.ShadowDistance);
            var scY = YUp(sc);
            renderer.EnableShadows = true;
            renderer.ShadowBounds = (new GlVec3(scY.X, scY.Y, scY.Z), sr);
            Console.WriteLine($"   sun shadow: map over a {sr:0}-unit sphere at ({sc.X:0}, {sc.Y:0}, {sc.Z:0}), ground {groundZ:0}, reach {SceneWeather.ShadowDistance:0}");
        }

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
        graphicsShot?.TrackTargets(fbo, colorTex, depthRb, msFbo, msColorRb, msDepthRb);
        shotResources.TrackTargets(fbo, colorTex, depthRb, msFbo, msColorRb, msDepthRb);
        int drawFbo = msFbo != 0 ? msFbo : fbo;

        // viewprobe's own wire pass, installed once built. It takes the view/projection to draw
        // with: the static path hands it the entry camera's fixed matrices; the interactive loop
        // hands it each frame's.
        Action<OpenTK.Mathematics.Matrix4, OpenTK.Mathematics.Matrix4>? wireOverlay = null;

        // readColour false: the colour buffer is not read back (the depth-measurement mode's measurement draws never use it).
        byte[] Draw(float[]? depthOut = null, bool singleSample = false, bool readColour = true)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, singleSample ? fbo : drawFbo);
            GL.Viewport(0, 0, sizeW, sizeH);
            ShotGlState.Establish(sizeW, sizeH);   // F-G5-1: nothing inherited from a previous shot or pass
            GL.ClearColor(skyColor.X, skyColor.Y, skyColor.Z, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            graphicsCell?.ObserveDraw(graphicsShot);   // measurement only (Gate B4, G2 RED); inert without a RED session
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
            var px = readColour ? new byte[sizeW * sizeH * 4] : Array.Empty<byte>();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            if (readColour) GL.ReadPixels(0, 0, sizeW, sizeH, PixelFormat.Rgba, PixelType.UnsignedByte, px);
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
            // Total unknown on purpose: how many shapes the visibility pass reaches depends on the
            // budget and on what the camera can see, and a made-up total is worse than none (§5).
            ViewProbe.RenderStages.Open(ViewProbe.RenderStages.Report,
                "the occlusion report: per-shape visibility, then writing it", null, "shape draws");
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
                Draw(plainDepth, singleSample: true, readColour: depthMeasurement is null);
                string DepthHash(float[] depth) => ViewProbe.MaskEvidence.DepthHash(depth);
                occl.SceneDepthHash = DepthHash(plainDepth);
                graphicsShot?.SceneDepth(occl.SceneDepthHash);   // G3 RED, measurement only
                var evidenceDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!,
                    Path.GetFileNameWithoutExtension(outPath) + "-audit-evidence");
                if (Directory.Exists(evidenceDir)) throw new System.IO.IOException("Refusing to replace audit evidence: " + evidenceDir);
                Directory.CreateDirectory(evidenceDir);
                occl.SceneDepthPath = Path.Combine(evidenceDir, "scene-depth-f32.bin");
                File.WriteAllBytes(occl.SceneDepthPath, System.Runtime.InteropServices.MemoryMarshal.AsBytes(plainDepth.AsSpan()).ToArray());
                // --occlusion-query: the shapes are counted on the GPU (OcclusionQuery.cs). The scene depth is copied once,
                // and its sampled values are checked bit for bit against the ReadPixels floats above before any count is used.
                ViewProbe.OcclusionQueryPass? query = null;
                if (depthMeasurement is { OcclusionQuery: true })
                {
                    query = new ViewProbe.OcclusionQueryPass(sizeW, sizeH, shotResources.Track);
                    query.CopySceneDepth(fbo);
                    var (convPixels, convMismatches) = query.ConversionCheck(plainDepth);
                    depthMeasurement.ConversionPixels = convPixels;
                    depthMeasurement.ConversionMismatches = convMismatches;
                    if (convMismatches > 0)
                        throw new InvalidOperationException($"the occlusion query's sampled scene depth differs from the read-back depth "
                            + $"in {convMismatches} of {convPixels} pixels, so its counts would not be the CPU compare's; refusing this view (run without {ViewProbe.OcclusionQueryPass.Flag}).");
                }
                // The isolation universe is what the renderer draws, not the shot's own list: with a retained scene
                // (--reuse-cell) the static meshes live in the renderer but never in `meshes`, and a shape measured
                // against them is measured against the whole scene (Gate B6, W-G3-1). On the per-shot path the
                // renderer holds exactly `meshes` plus the borrowed NPC meshes at this point, so nothing changes there.
                var allMeshes = renderer.Meshes.Concat(npcActors.SelectMany(a => a.Meshes)).Distinct().ToArray();
                var savedVisibility = allMeshes.ToDictionary(m => m, m => m.IsRendering);
                var ownDepth = new float[sizeW * sizeH];
                // Created HERE, beside the depth buffers whose contents its keys identify, and
                // thrown away with them. A view's masks cannot reach the next view's because the
                // cache does not outlive the buffers it is keyed on - not because anything
                // remembers to clear it. `plainDepth` is per view, so a key from another view would
                // not mean the same mask.
                var maskCache = new ViewProbe.MaskEvidenceCache(ViewProbe.MaskEvidence.VerifyRequested);
                occl.MarkVisible(part =>
                {
                    if (occl.VisibilityDraws >= occl.VisibilityBudget)
                        return new ShapeVisibility(0, 0, 0, "explicit per-view visibility draw budget exhausted");
                    if (part.Meshes.Count == 0) return new ShapeVisibility(0, 0, 0, "no shape geometry to rasterize");
                    try
                    {
                        var isolated = part.Meshes.ToHashSet();
                        foreach (var m in allMeshes) m.IsRendering = savedVisibility[m] && isolated.Contains(m);
                        Draw(query is null ? ownDepth : null, singleSample: true, readColour: depthMeasurement is null);
                        occl.VisibilityDraws++;
                        (int Raster, int Visible)? counted = null;
                        if (query is not null)
                        {
                            counted = query.Count(fbo);
                            depthMeasurement!.QueriedShapes++;
                            if (!DepthMeasurement.ReadsBack(depthMeasurement, counted.Value.Visible))
                            {
                                // No visible pixel: no read-back, no hash, no mask (the report says so in measurementMode).
                                part.IsolatedDepthHash = null;
                                var queried = new ShapeVisibility(counted.Value.Raster, 0, counted.Value.Raster, null);
                                graphicsShot?.IsolationDraw(occl.VisibilityDraws, isolated, renderer, null,
                                    queried.RasterPixels, queried.VisiblePixels, queried.HiddenPixels);   // G3 RED, measurement only
                                return queried;
                            }
                            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                            GL.ReadPixels(0, 0, sizeW, sizeH, PixelFormat.DepthComponent, PixelType.Float, ownDepth);
                        }
                        part.IsolatedDepthHash = DepthHash(ownDepth);
                        var measured = ShapeVisibility.Compare(ownDepth, plainDepth);
                        if (counted is { } c)
                        {
                            depthMeasurement!.CpuCheckedShapes++;
                            if (measured.Issue is not null || measured.RasterPixels != c.Raster || measured.VisiblePixels != c.Visible)
                                throw new InvalidOperationException($"the occlusion query and the CPU depth compare disagree on shape block {part.Block} "
                                    + $"(visibility draw {occl.VisibilityDraws}): query raster/visible {c.Raster}/{c.Visible}, CPU "
                                    + $"{measured.RasterPixels}/{measured.VisiblePixels}{(measured.Issue is null ? "" : " (" + measured.Issue + ")")}; "
                                    + $"refusing this view (run without {ViewProbe.OcclusionQueryPass.Flag}).");
                        }
                        graphicsShot?.IsolationDraw(occl.VisibilityDraws, isolated, renderer, part.IsolatedDepthHash,
                            measured.RasterPixels, measured.VisiblePixels, measured.HiddenPixels);   // G3 RED, measurement only
                        // --depth-measurement: no picture for a shape with no visible pixel; the scorer opens none.
                        if (!DepthMeasurement.WritesMask(depthMeasurement, measured.VisiblePixels)) return measured;
                        var mask = ViewProbe.MaskEvidence.BuildMask(ownDepth, plainDepth);
                        part.VisibilityMask = Path.Combine(evidenceDir, $"shape-{occl.VisibilityDraws:D4}-block-{part.Block}.png");
                        // Keyed on the hash of the buffer the mask was just built from, which this
                        // shape has already paid for above. Most of a view's shapes produce a
                        // picture the view has already encoded.
                        maskCache.Write(part.IsolatedDepthHash, mask, sizeW, sizeH, part.VisibilityMask);
                        return measured;
                    }
                    finally { foreach (var (m, visible) in savedVisibility) m.IsRendering = visible; }
                });
                // Only when verification was asked for: a healthy run's console says exactly what
                // it said before. A mismatch has already been reported on stderr, per mask.
                if (maskCache.Verifying)
                {
                    Console.WriteLine("   " + maskCache.VerifySummary());
                    if (maskCache.Mismatches > 0)
                        throw new InvalidOperationException(
                            $"mask evidence verification failed: {maskCache.Mismatches} of {maskCache.Hits} "
                            + "cache hit(s) re-encoded to different bytes");
                }
            }
            else occl.SceneIssues.Add("interactive view has no captured shape visibility measurement");
            // BEFORE the sink, because the sink WRITES the report and the report carries the
            // unreadable-asset block. Classifying only at the census - which prints after this -
            // left every report's block empty, which is how this ordering was found.
            ClassifyUnreadable();
            planeReport(occl);
            ViewProbe.RenderStages.Progress(occl.VisibilityDraws);
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
                var m0 = BuildFill(group.Triangles, new Vector4(colour.X, colour.Y, colour.Z, alpha), "navmesh-fill", "dynamic-nav-fill");
                m0.IsDecal = true;             // same insertion-ordered channel, added after the ground
                renderer.AddMesh(m0); graphicsShot?.MeshAdded("dynamic-nav-fill", m0); meshes.Add(m0);
                interactive?.NavFill.Add(m0);
            }
            if (redTris.Count > 0)
            {
                var m1 = BuildFill(redTris, new Vector4(0.95f, 0.15f, 0.08f, MathF.Min(0.55f, alpha + 0.2f)), "navmesh-fill-red", "dynamic-nav-red-fill");
                m1.IsDecal = true;
                renderer.AddMesh(m1); graphicsShot?.MeshAdded("dynamic-nav-red-fill", m1); meshes.Add(m1);
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
                    $"npc-puck:{actor.Label}", "dynamic-npc-puck");
                disc.IsDecal = true;
                renderer.AddMesh(disc); graphicsShot?.MeshAdded("dynamic-npc-puck", disc); meshes.Add(disc);
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
        float ViewDist(float winZ)
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
        // G3 RED (test-only): each layer's label, recorded only in a RED session so every overlay family is hashed
        // on its own at its BufferData. Without a session AddLayer is exactly layers.Add.
        var layerLabels = graphicsShot is null ? null
            : new Dictionary<float[], string>(ReferenceEqualityComparer.Instance);
        void AddLayer(string label, (float[] Data, bool DepthTested, bool CullBack, int Nav, float Alpha) layer)
        {
            layers.Add(layer);
            if (layerLabels is not null) layerLabels[layer.Data] = label;
        }
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
                Console.WriteLine($"subject-surface-refused: {key}: "
                    + (surfaceShapes.TryGetValue(key, out var sc) ? $"{sc.Refused} of {sc.Total} shape(s) " : "")
                    + "alpha, effect, marker or non-depth-writing; participant coverage incomplete");
            if (subjectSurface.Count > 0) AddLayer("subject-surface", (subjectSurface.ToArray(), true, true, 0, refSurfaceOpacity));
            if (subjectSurfaceDoubleSided.Count > 0) AddLayer("subject-surface-double-sided", (subjectSurfaceDoubleSided.ToArray(), true, false, 0, refSurfaceOpacity));
        }
        // Highlight halos: the SAME hull buffer is drawn twice — solid depth-tested rim here,
        // then again as the translucent Greater-pass ghost (see the draw loop).
        float[]? haloData = haloTris.Count > 0 ? haloTris.ToArray() : null;
        if (haloData is not null) AddLayer("halo", (haloData, true, true, 0, 1f));
        // The grid goes on FIRST, so every later layer paints over it: it is the ruling under the
        // picture, and a scale that hid the navmesh or the route it exists to measure would have
        // the priority exactly backwards. Depth test OFF — a reference line you can only see on
        // open floor is not a reference; it has to cross the pillars too.
        if (gridSpacing > 0f && gridRibbon.Count > 0) AddLayer("grid", (gridRibbon.ToArray(), false, false, 0, 0.85f));
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
            if (ribbon.Count > 0) AddLayer("seams", (ribbon.ToArray(), true, false, 0, 1f));
        }
        if (wantNav)
        {
            if (wireMode && navTris.Count > 0)
            {
                foreach (var group in healthyGroups)
                {
                    var (s, d) = BuildWire(group.Triangles, GroupColour(group), 2.2f);
                    if (s.Count > 0) AddLayer("nav-wire-solid", (s.ToArray(), true, false, 1, 1f));
                    if (d.Count > 0) AddLayer("nav-wire-dash", (d.ToArray(), false, false, 1, 1f));
                }
            }
            // The finding is ALWAYS outlined, wider than the healthy wire, in every mode that
            // draws navmesh at all.
            if (redTris.Count > 0)
            {
                var (s, d) = BuildWire(redTris, red, 3.4f);
                if (s.Count > 0) AddLayer("red-nav-solid", (s.ToArray(), true, false, 2, 1f));
                if (d.Count > 0) AddLayer("red-nav-dash", (d.ToArray(), false, false, 2, 1f));
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
                AddLayer("cell-outline", (outline.ToArray(), false, false, 5, 1f));   // tag 5: a host can hide it
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
            AddLayer("nav-diff-spheres", (spheres.ToArray(), false, true, 0, 1f));
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
            AddLayer("npc-rim", (rim.ToArray(), false, false, 0, 1f));
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
            if (rDisc.Count > 0) AddLayer("route-disc", (rDisc.ToArray(), false, false, 3, 0.35f));
            if (rSolid.Count > 0) AddLayer("route-solid", (rSolid.ToArray(), true, false, 3, 1f));
            if (rDash.Count > 0) AddLayer("route-dash", (rDash.ToArray(), false, false, 3, 1f));
            if (rFlat.Count > 0) AddLayer("route-arrow", (rFlat.ToArray(), false, false, 3, 1f));
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
                if (quadFill.Count > 0) AddLayer("plane-quad", (quadFill.ToArray(), true, false, 4, PlaneOverlay.QuadAlpha));
                if (boxFill.Count > 0) AddLayer("plane-box", (boxFill.ToArray(), true, false, 4, PlaneOverlay.BoxAlpha));
                if (pSolid.Count > 0) AddLayer("plane-solid", (pSolid.ToArray(), true, false, 4, 1f));
                if (pDash.Count > 0) AddLayer("plane-dash", (pDash.ToArray(), false, false, 4, 1f));
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
            graphicsShot?.TrackOverlayObjects(wireProg, wireVao, wireVbo);
            shotResources.TrackOverlay(wireProg, wireVao, wireVbo);
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
                    if (nav == 5 && interactive is { ShowCellOutline: false }) continue;
                    // At alpha 1 this is what the loop always did; below it, ordinary source-alpha
                    // blending, which is the whole mechanism behind a translucent overlay layer.
                    if (layerAlpha < 1f)
                    {
                        GL.Enable(EnableCap.Blend);
                        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                        GL.Uniform1(alphaLoc, layerAlpha);
                    }
                    else { GL.Disable(EnableCap.Blend); GL.Uniform1(alphaLoc, 1.0f); }
                    // the cell outline is built drawn through everything; a host can have it hidden behind what is in front
                    if (depthTested || (nav == 5 && interactive is { CellOutlineThrough: false })) { GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Lequal); }
                    else GL.Disable(EnableCap.DepthTest);
                    if (cullBack) { GL.Enable(EnableCap.CullFace); GL.CullFace(CullFaceMode.Back); }
                    else GL.Disable(EnableCap.CullFace);
                    GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StreamDraw);
                    graphicsShot?.RawBufferUploaded(nav switch
                    {
                        1 => "nav-wire",
                        2 => "red-nav-wire",
                        3 => "route",
                        4 => "planes",
                        _ => "grid-seam-outline-mask"
                    }, wireVbo, data, layerLabels?.GetValueOrDefault(data));
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
                    graphicsShot?.RawBufferUploaded("subject-mask-ghost", wireVbo, ghostData);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, ghostData.Length / 9);
                    if (ghostNearData is not null)
                    {
                        // Deep-occlusion boost: near faces pass GREATER only when the occluder
                        // is in front of the whole hull (a wall), never for the object's own
                        // surface or its jamb — so only the truly hidden case gets brighter.
                        GL.Uniform1(alphaLoc, 0.25f);
                        GL.BufferData(BufferTarget.ArrayBuffer, ghostNearData.Length * sizeof(float), ghostNearData, BufferUsageHint.StreamDraw);
                        graphicsShot?.RawBufferUploaded("subject-mask-near", wireVbo, ghostNearData);
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

        // All shot-local meshes and raw-overlay owners now exist. BufferData itself occurs in
        // Draw and is separately observed at the exact call boundary above.
        graphicsShot?.Checkpoint("after-dynamic");

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
        Console.WriteLine("   " + TexturePolicy.UploadSummary(new[] { textures, graphicsCell?.InstallTextures }, cache));
        Console.WriteLine($"   textured: {shapes:N0} shapes ({texturedShapes:N0} with a real diffuse), {missingMesh:N0} refs without a resolvable mesh"
                            + $"; {sizeW}x{sizeH}, {(samples >= 2 ? samples + "x msaa" : "no msaa")}");
            if (depthMeasurement is not null)
                Console.WriteLine($"   depth-measurement: {depthMeasurement.KeptDiffuse.Count:N0} alpha-tested diffuse(s) loaded "
                                + $"({depthMeasurement.KeptDiffuse.Values.Sum():N0} file bytes); every other texture not loaded; masks "
                                + (depthMeasurement.KeepAllMasks ? "for every assessed shape" : "only for shapes with visible pixels"));
            if (cullAbove is { } cz2)
            {
                meshReadHooks?.ObserveCull?.Invoke(cullTrisDropped, cullShapesDropped);
                Console.WriteLine($"   cull-above z {cz2:0}: {cullTrisDropped:N0} static triangle(s) removed, "
                                + $"{cullShapesDropped:N0} shape(s) wholly above the cut (terrain/nav/actors/overlay untouched)");
            }
            if (emissiveShapes > 0)
                Console.WriteLine($"   emissive shapes: {emissiveShapes:N0} (SLSF1_Own_Emit, glow map where flagged; External_Emittance shapes stay unlit)");
            if (envMappedShapes > 0)
                Console.WriteLine($"   env-mapped shapes: {envMappedShapes:N0} (slot-4 cube map, slot-5 mask, NIF envMapScale)");
            if (editorMarkerShapes > 0)
                Console.WriteLine($"   editor-marker shapes: {editorMarkerShapes:N0} not drawn (shape named EditorMarker; the game hides it)");
            if (fxStaticShapes > 0)
                Console.WriteLine($"   static effect shapes: {fxStaticShapes:N0} DRAWN on {fxStaticRefCount:N0} ref(s) "
                                + "— engine effect shading, frozen at the authored frame (--no-fx drops them)");
            if (coveringShapes + projectedShapes > 0)
            {
                Console.WriteLine($"   projected coverings (MATO): {projectedShapes:N0} single-pass shape(s) covered per pixel, "
                                + $"{coveringShapes:N0} shell(s), on {coveringRefs:N0} ref(s) "
                                + $"of {coveringByMato.Count} material(s) — STAT.Material + MaxAngle, --no-coverings to drop");
                foreach (var kv in coveringByMato.OrderByDescending(k => k.Value))
                    Console.WriteLine($"      {kv.Value,5} shape(s)  {kv.Key}");
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
            // WHICH OF THOSE WERE FOUND AND COULD NOT BE READ, decided BEFORE the line below is
            // printed: the two counts above cannot tell an asset nobody shipped from one whose file
            // will not parse, and a line that said "could NOT find" about a file this tool has just
            // opened would be false. The classifier redoes each failed read to decide, and prints
            // nothing when every one of them was simply absent - so a healthy run's console, this
            // line included, is unchanged to the character.
            ClassifyUnreadable();
            var missingTexturePaths = graphicsCell is null
                ? textures.MissingTexturePaths.ToArray()
                : retainedMissingTexturePaths.ToArray();
            if (missingTexturePaths.Length > 0)
            {
                int unreadableTextures = UnreadableAssets.ShotTextureCount;
                Console.WriteLine($"   textures the resolver could NOT find (white fallback, counted per rule 6): {missingTexturePaths.Length:N0}"
                                + (unreadableTextures > 0
                                   ? $" — {unreadableTextures:N0} of them WERE found and could not be read, listed below"
                                   : ""));
            }
            UnreadableAssets.PrintCensus();
            UnreadableAssets.PrintNotDrawnByRule();
        }

        // Idempotent within a render: the report sink and the census both need the answer, and the
        // sink runs first. Classify() rebuilds this render's list each time it is called, so a
        // second call cannot double-count.
        void ClassifyUnreadable()
            => UnreadableAssets.Classify(resolver,
                graphicsCell is null ? textures.MissingTexturePaths.ToArray() : retainedMissingTexturePaths.ToArray(),
                noGeometry, geometryInputIssues, noGeometryAt);

        if (MapTiles.Requested is { } mapTiles)
        {
            PrintBuildCensus();
            MapTiles.Render(renderer, mapTiles);
            shotResources.Dispose();
            graphicsCell?.EndShot();
            return shapes;
        }

        if (interactive is not null)
        {
            // BEFORE the window opens, not after it closes: the console is what a person reads
            // while deciding where to fly, and a census printed on exit is a census nobody used.
            PrintBuildCensus();

            // the owners a host's later cell slices build through (LiveScene.cs); disposed below, after the loop
            interactive.Live = new LiveSession { Renderer = renderer, Textures = textures, Assets = new CellAssets(resolver, cache), Mesh = meshState };
            if (InteractiveViewer.Host is { } host) host(interactive, renderer, cam, wireOverlay);
            else InteractiveViewer.Run(interactive, renderer, cam, wireOverlay);

            shotResources.Dispose();
            graphicsCell?.EndShot();
            return shapes;
        }

        float[]? pickDepth = picks.Count > 0 || npcActors.Count > 0 || depthDumpPath is not null ? new float[sizeW * sizeH] : null;
        var frameSw = System.Diagnostics.Stopwatch.StartNew();
        ApplyHide();
        var pixels = Draw(pickDepth);
        Console.WriteLine($"   frame drawn: {frameSw.Elapsed.TotalMilliseconds:0} ms after the scene was built");
        // --lod: the final frame's draw time (GL.Finish inside Draw), since LOD is what adds draws.
        if (DistantLodLayer.Requested) Console.WriteLine($"   lod timing: final frame drawn in {frameSw.Elapsed.TotalMilliseconds:0} ms");
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

        // --occluders-of / --coverage-of (CaptureViews.cs): isolated draws after the frame, on the single-sample buffer. The
        // overlay, the sky gradient and the image space finish are off for them, and every saved state is restored after.
        if ((CaptureViews.CoverageOf.Count > 0 || CaptureViews.OccludersOf.Count > 0) && interactive is null)
        {
            var allMeshesC = renderer.Meshes.Concat(npcActors.SelectMany(a => a.Meshes)).Distinct().ToArray();
            var savedVisC = allMeshesC.ToDictionary(m => m, m => m.IsRendering);
            var savedClearC = renderer.ClearColor; var savedSkyC = skyColor; var savedGradC = renderer.SkyGradient;
            var savedIsC = renderer.ImageSpace; var savedWireC = wireOverlay;
            var invVpC = viewProj.Inverted();
            float[] IsolatedDepth(IReadOnlyCollection<GlMesh> iso, bool bothFaces = false)
            {
                var set = iso.ToHashSet();
                var saved = iso.ToDictionary(m => m, m => (m.DepthWrite, m.IsDoubleSided));
                var depth = new float[sizeW * sizeH];
                try
                {
                    foreach (var m in allMeshesC) m.IsRendering = savedVisC[m] && set.Contains(m);
                    foreach (var m in iso) { m.DepthWrite = true; if (bothFaces) m.IsDoubleSided = true; }
                    Draw(depth, singleSample: true, readColour: false);
                }
                finally { foreach (var (m, st) in saved) { m.DepthWrite = st.DepthWrite; m.IsDoubleSided = st.IsDoubleSided; } }
                return depth;
            }
            Vector3 Unproject(int i, float d)
            {
                int bx = i % sizeW, gy = i / sizeW;
                float nx = (bx + 0.5f) / sizeW * 2f - 1f, ny = (gy + 0.5f) / sizeH * 2f - 1f;
                var w4 = GlVec4.TransformRow(new GlVec4(nx, ny, d * 2f - 1f, 1f), invVpC);
                var wy = new Vector3(w4.X / w4.W, w4.Y / w4.W, w4.Z / w4.W);
                return new Vector3(wy.X, -wy.Z, wy.Y);
            }
            string Stem(string key) => Path.GetFileNameWithoutExtension(outPath) + "-coverage-" + key.Replace(':', '_').Replace(' ', '_').Replace("'", "");
            void SaveGrey(string name, Func<int, byte> value)
            {
                var img = new byte[sizeW * sizeH];
                for (int y = 0; y < sizeH; y++)
                    for (int x = 0; x < sizeW; x++) img[(sizeH - 1 - y) * sizeW + x] = value(y * sizeW + x);
                using var gi = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.L8>(img, sizeW, sizeH);
                gi.SaveAsPng(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, name));
            }
            try
            {
                wireOverlay = null;
                renderer.SkyGradient = null; renderer.ImageSpace = null;
                var sceneDepthC = new float[sizeW * sizeH];
                Draw(sceneDepthC, singleSample: true, readColour: false);    // the frame's own depth: hidden refs stay out

                // ---- --occluders-of: every drawn ref nearer than the named one over its own pixels ----
                foreach (var key in CaptureViews.OccludersOf)
                {
                    var own = occl.Refs.Where(o => o.Ref.RefKey.ToString() == key).SelectMany(o => o.Meshes)
                                       .Where(m => savedVisC.TryGetValue(m, out var v) && v).ToList();
                    if (own.Count == 0) { Console.WriteLine($"occluders-of {key}: not drawn in this frame"); continue; }
                    var ownD = IsolatedDepth(own);
                    int covered = 0, visible = 0, x0 = sizeW, x1 = -1, y0 = sizeH, y1 = -1;
                    float farD = 0f;
                    for (int i = 0; i < ownD.Length; i++)
                    {
                        if (ownD[i] >= 0.999999f) continue;
                        covered++;
                        if (ownD[i] <= sceneDepthC[i] + 1e-6f) visible++;
                        int x = i % sizeW, y = i / sizeW;
                        x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                        farD = MathF.Max(farD, ownD[i]);
                    }
                    Console.WriteLine($"occluders-of {key}: covers {covered} px, visible {visible} px");
                    if (covered == 0) continue;
                    var ownSet = own.ToHashSet();
                    var refMeshes = new HashSet<GlMesh>();
                    var rows = new List<(string Key, int Px, bool Translucent)>();
                    foreach (var o in occl.Refs)
                    {
                        var ms = o.Meshes.Where(m => savedVisC.TryGetValue(m, out var v) && v && !ownSet.Contains(m)).ToList();
                        foreach (var m in ms) refMeshes.Add(m);
                        if (ms.Count == 0 || o.Ref.RefKey.ToString() == key) continue;
                        // the candidate's box must reach the participant's screen rectangle (any corner behind the eye: test it)
                        bool behind = false; int bx0 = sizeW, bx1 = -1, by0 = sizeH, by1 = -1;
                        for (int c = 0; c < 8; c++)
                        {
                            var p = new Vector3((c & 1) == 0 ? o.Min.X : o.Max.X, (c & 2) == 0 ? o.Min.Y : o.Max.Y, (c & 4) == 0 ? o.Min.Z : o.Max.Z);
                            var clip = GlVec4.TransformRow(new GlVec4(p.X, p.Z, -p.Y, 1f), viewProj);
                            if (clip.W <= 0.01f) { behind = true; break; }
                            int sx = (int)((clip.X / clip.W * 0.5f + 0.5f) * sizeW), sy = (int)((clip.Y / clip.W * 0.5f + 0.5f) * sizeH);
                            bx0 = Math.Min(bx0, sx); bx1 = Math.Max(bx1, sx); by0 = Math.Min(by0, sy); by1 = Math.Max(by1, sy);
                        }
                        if (!behind && (bx1 < x0 || bx0 > x1 || by1 < y0 || by0 > y1)) continue;
                        var d = IsolatedDepth(ms);
                        int n = 0;
                        for (int i = 0; i < d.Length; i++)
                            if (ownD[i] < 0.999999f && d[i] < ownD[i] - 1e-6f) n++;
                        if (n > 0) rows.Add((o.Ref.RefKey.ToString(), n, ms.All(m => !m.DepthWrite)));
                    }
                    foreach (var (k, n, tr) in rows.OrderByDescending(r => r.Px))
                        Console.WriteLine($"occluders-of {key}: occluder {k} {n} px{(tr ? " translucent (writes no depth)" : "")}");
                    var other = allMeshesC.Where(m => savedVisC[m] && !refMeshes.Contains(m) && !ownSet.Contains(m)).ToList();
                    if (other.Count > 0)
                    {
                        var od = IsolatedDepth(other);
                        int n = 0;
                        for (int i = 0; i < od.Length; i++) if (ownD[i] < 0.999999f && od[i] < ownD[i] - 1e-6f) n++;
                        Console.WriteLine($"occluders-of {key}: other geometry (terrain, water, distant LOD, actors) {n} px");
                    }
                }

                // ---- --coverage-of: a see-through ref's drawn coverage ----
                renderer.SkyGradient = null; renderer.ImageSpace = null;
                foreach (var spec in CaptureViews.CoverageOf)
                {
                    var (key, point) = CaptureViews.CoverageSpec(spec);
                    if (!coverageMeshes.TryGetValue(key, out var own) || own.Count == 0)
                    {
                        // which of three different things: not drawn at all in this setup, drawn but every shape opaque, or
                        // placed with no shape built
                        var placed = occl.Refs.Where(o => o.Ref.RefKey.ToString() == key).ToList();
                        string why = placed.Count == 0 ? "not-drawn: it is not a drawn reference in this setup (not placed in the winning state, disabled, or no model)"
                                   : placed.All(o => o.Meshes.Count == 0) ? "not-drawn: it is placed but the renderer built no shape of it"
                                   : CaptureViews.Hide.Contains(key) ? "not-drawn: it is hidden in this frame"
                                   : "opaque: every drawn shape of it is opaque, so --ref-surface marks it";
                        Console.WriteLine($"coverage-of {key}: {why}");
                        continue;
                    }
                    int switchedOff = own.Count(m => !savedVisC.TryGetValue(m, out var vis0) || !vis0);
                    var iso = own.ToHashSet();
                    foreach (var m in allMeshesC) m.IsRendering = savedVisC[m] && iso.Contains(m);
                    skyColor = new Vector3(0f, 0f, 0f); renderer.ClearColor = new GlVec3(0f, 0f, 0f);
                    var blk = Draw(null, singleSample: true);
                    skyColor = new Vector3(1f, 1f, 1f); renderer.ClearColor = new GlVec3(1f, 1f, 1f);
                    var wht = Draw(null, singleSample: true);
                    var ownDepth = IsolatedDepth(own);
                    var alpha = CaptureViews.Alpha(blk, wht, sizeW, sizeH);
                    int drawn = 0, front = 0, pass = 0, passFront = 0, depthless = 0, raster = 0;
                    var mask = new byte[sizeW * sizeH];
                    var hist = new int[10];
                    for (int i = 0; i < alpha.Length; i++)
                    {
                        if (ownDepth[i] < 0.999999f) raster++;
                        float av = alpha[i];
                        if (av <= 1f / 255f) continue;
                        drawn++;
                        hist[Math.Min(9, (int)(av * 10f))]++;
                        bool hasOwn = ownDepth[i] < 0.999999f;
                        if (!hasOwn) depthless++;
                        bool inFront = hasOwn && ownDepth[i] <= sceneDepthC[i];
                        if (inFront) front++;
                        if (av >= CaptureViews.CoverageAlphaMin) { pass++; if (inFront) { passFront++; mask[i] = 255; } }
                    }
                    // nothing rasterised: outside the frustum, or single-sided and seen from behind (back faces culled, as the
                    // engine culls them). One more forced-depth draw with both faces tells the two apart.
                    int rasterBoth = raster;
                    if (raster == 0)
                    {
                        var both = IsolatedDepth(own, bothFaces: true);
                        rasterBoth = both.Count(v => v < 0.999999f);
                    }
                    string state = switchedOff == own.Count ? $"not-rendered: the renderer holds all {own.Count} of its shapes switched off in this view"
                                 : raster == 0 && rasterBoth > 0 ? $"back-facing: single-sided and seen from behind; {rasterBoth} px rasterise only with back faces drawn, which the engine culls"
                                 : raster == 0 ? "outside-frustum: none of its triangles rasterised in this view, front or back"
                                 : drawn == 0 ? $"transparent: {raster} px rasterised, every one at alpha <= 1/255"
                                 : passFront == 0 ? $"below-threshold: drawn, but no pixel is both in front of the scene and at alpha {CaptureViews.CoverageAlphaMin:0.##}"
                                 : "marked";
                    Console.WriteLine($"coverage-of {key}: {state}");
                    // the blend the measured value means: over SRC_ALPHA / INV_SRC_ALPHA it is the opacity a; over an additive
                    // (dst ONE) shape it is the colour the shape adds, c * a. A frame composites back as black + behind * (1 - a)
                    // in the first case and black + behind in the second (tests/test_capture_fallbacks.py CF5).
                    string blend = own.All(m => m.DstBlendIndex == 0) ? "additive" : own.All(m => m.DstBlendIndex == 7) ? "alpha" : "mixed";
                    Console.WriteLine($"coverage-of {key}: blend {blend}");
                    Console.WriteLine($"coverage-of {key}: {own.Count} non-opaque shape(s); rasterised {raster} px; drawn {drawn} px (alpha > 1/255), "
                                    + $"{front} of them in front of the opaque scene, {pass} at alpha >= {CaptureViews.CoverageAlphaMin:0.##}, "
                                    + $"{passFront} both (the mark); {depthless} drawn px gave no own depth");
                    Console.WriteLine($"coverage-of {key}: alpha histogram by tenths " + string.Join(" ", hist));
                    var stem = Stem(key);
                    SaveGrey(stem + "-mask.png", i => mask[i]);
                    SaveGrey(stem + "-alpha.png", i => (byte)Math.Clamp(alpha[i] * 255f + 0.5f, 0f, 255f));
                    if (CaptureViews.CoverageDump)
                    {
                        var flip = new byte[sizeW * sizeH * 4];
                        for (int y = 0; y < sizeH; y++)
                            Array.Copy(blk, y * sizeW * 4, flip, (sizeH - 1 - y) * sizeW * 4, sizeW * 4);
                        for (int i = 3; i < flip.Length; i += 4) flip[i] = 255;
                        using var bi = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(flip, sizeW, sizeH);
                        bi.SaveAsPng(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, stem + "-black.png"));
                    }
                    Console.WriteLine($"coverage-of {key}: mask {stem}-mask.png, alpha {stem}-alpha.png (image rows top-down)");
                    // the mark's world points at the --pick pixels: the participant's own nearest surface there
                    foreach (var (ix, iy) in picks)
                    {
                        if (ix < 0 || ix >= sizeW || iy < 0 || iy >= sizeH) continue;
                        int gi = (sizeH - 1 - iy) * sizeW + ix;
                        if (mask[gi] == 0) continue;
                        var ws = Unproject(gi, ownDepth[gi]);
                        Console.WriteLine($"coverage pick {key} {ix},{iy}: {ws.X:0},{ws.Y:0},{ws.Z:0}  alpha {alpha[gi]:0.000}  "
                                        + $"{Vector3.Distance(ws, cam.Eye):0} u from the eye");
                    }
                    if (point is { } pt)
                    {
                        var sp = ScreenOf(new Vector3(pt.X, pt.Z, -pt.Y));
                        if (sp is null) Console.WriteLine($"coverage-at {key} {pt.X:0},{pt.Y:0},{pt.Z:0}: off the frame");
                        else
                        {
                            int gi = sp.Value.Y * sizeW + sp.Value.X;
                            int depthIn = 0;
                            while (depthIn < 64)
                            {
                                int r = depthIn + 1, px = sp.Value.X, py = sp.Value.Y;
                                if (px - r < 0 || px + r >= sizeW || py - r < 0 || py + r >= sizeH) break;
                                if (mask[py * sizeW + px - r] == 0 || mask[py * sizeW + px + r] == 0
                                    || mask[(py - r) * sizeW + px] == 0 || mask[(py + r) * sizeW + px] == 0) break;
                                depthIn++;
                            }
                            Console.WriteLine($"coverage-at {key} {pt.X:0},{pt.Y:0},{pt.Z:0}: pixel {sp.Value.X},{sizeH - 1 - sp.Value.Y} "
                                            + $"{(mask[gi] != 0 ? $"in the mark, {depthIn} px inside" : "outside the mark")}");
                        }
                    }
                }
            }
            finally
            {
                foreach (var (m, v) in savedVisC) m.IsRendering = v;
                renderer.ClearColor = savedClearC; skyColor = savedSkyC; renderer.SkyGradient = savedGradC;
                renderer.ImageSpace = savedIsC; wireOverlay = savedWireC;
            }
        }
        if (wireProg != 0)
        {
            graphicsShot?.OverlayDeleteState(wireProg, GL.GetInteger(GetPName.CurrentProgram),
                GL.GetInteger(GetPName.VertexArrayBinding));
            GL.UseProgram(0); GL.BindVertexArray(0);   // unbind first, or the program's deletion is only deferred
            GL.DeleteProgram(wireProg); GL.DeleteVertexArray(wireVao); GL.DeleteBuffer(wireVbo);
        }

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

        graphicsShot?.Checkpoint("before-encode");
        // THE FRAME SAYS WHAT IT LACKS. Classified here, before the save, so a picture missing an
        // asset that was found and could not be read carries the names inside the file - the
        // console and the run record can be separated from a PNG, its own metadata cannot. Nothing
        // is added to a frame that lacks nothing. Classify is idempotent within a render, so the
        // census below re-reads the same answer.
        ClassifyUnreadable();
        SaveFrame(pixels, sizeW, sizeH, capLines, colourCaptionLines, pngTextMeta, outPath,
                  gridSpacing > 0f ? gridLabels : null, gridColour3, reportMode,
                  UnreadableAssets.FrameIncompleteText(),
                  CaptureViews.ViewLabel(hiddenPresent.Count, cullAbove, gridSpacing),
                  hiddenPresent.Count > 0 ? "hidden: " + string.Join(" ", hiddenPresent) : null);

        shotResources.Dispose();
        graphicsCell?.EndShot();
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
        bool reportMode = false, string? incompleteMeta = null, string? viewLabel = null, string? viewMeta = null)
    {
        ViewProbe.RenderStages.Open(ViewProbe.RenderStages.Encode,
            "encoding the frame and writing it", 1, "frames");
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

        // ---- the view label (CaptureViews.cs): a frame that is not what the player sees says so, in report mode too --------
        // The one raster text report mode keeps. Bottom-left, on a dark strip; its rectangle is printed so a measurement can
        // leave those pixels out, and the label and the hidden list ride in the PNG's own metadata.
        if (viewLabel is { Length: > 0 } && font is not null)
        {
            var vf = font;
            var size = TextMeasurer.MeasureSize(viewLabel, new TextOptions(vf));
            int pad = Math.Max(2, (int)(6f * capScale));
            int rw = Math.Min(width, (int)MathF.Ceiling(size.Width) + 2 * pad), rh = Math.Min(height, (int)MathF.Ceiling(size.Height) + 2 * pad);
            int rx = 0, ry = height - rh;
            img.Mutate(x =>
            {
                x.Fill(SixLabors.ImageSharp.Color.FromRgba(8, 10, 14, 230), new SixLabors.ImageSharp.RectangleF(rx, ry, rw, rh));
                x.DrawText(viewLabel, vf, SixLabors.ImageSharp.Color.FromRgb(255, 214, 64), new SixLabors.ImageSharp.PointF(rx + pad, ry + pad));
            });
            Console.WriteLine($"view-label: {viewLabel} | rect {rx},{ry},{rw},{rh}");
            img.Metadata.GetPngMetadata().TextData.Add(new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData(
                "viewprobe-view", viewLabel + $" | rect {rx},{ry},{rw},{rh}" + (viewMeta is null ? "" : " | " + viewMeta), string.Empty, string.Empty));
        }
        else if (viewLabel is { Length: > 0 })
            throw new CaptureRefusal($"the frame needs the view label '{viewLabel}' and no system font was found to draw it; refusing an unlabelled picture");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        // Replication chunk: the camera pose (and the arguments that compose it) rides
        // inside the file as a PNG tEXt entry keyed "viewprobe", so a shot can be re-run
        // from the image alone even when it is cropped away from its console transcript.
        if (pngTextMeta is { Length: > 0 })
            img.Metadata.GetPngMetadata().TextData.Add(
                new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData("viewprobe", pngTextMeta, string.Empty, string.Empty));
        // the texture policy the frame was drawn under (TexturePolicy.cs): it changes texture pixels, so a frame says it
        img.Metadata.GetPngMetadata().TextData.Add(new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData(
            "viewprobe-textures", TexturePolicy.Current.Token, string.Empty, string.Empty));
        if (incompleteMeta is { Length: > 0 })
            img.Metadata.GetPngMetadata().TextData.Add(
                new SixLabors.ImageSharp.Formats.Png.Chunks.PngTextData("viewprobe-incomplete", incompleteMeta, string.Empty, string.Empty));
        img.SaveAsPng(outPath);
        ViewProbe.RenderStages.Advance();
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
        var nif = new NiflySharp.NifFile();
        if (nif.Load(nifPath) != 0) return;
        if (nif.GetRootNode() is not { } root) return;
        // the rotation's 9 floats in file order, as the renderer's NIF reader holds them
        var m = root.Rotation;
        var inv = new NifRootTransform(
            m.M11, m.M21, m.M31,
            m.M12, m.M22, m.M32,
            m.M13, m.M23, m.M33,
            root.Translation.X, root.Translation.Y, root.Translation.Z, root.Scale);

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
