using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CharacterViewer.Rendering;
using HousecarlCore;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ViewProbe;

/// <summary>RED-first proof for the next cell-scoped mesh slice. It reaches the mesh miss block
/// inside TexturedScene.RenderToPng with injected reads/builds, but deliberately adds no retention.</summary>
internal static class MeshReuseRed
{
    sealed record ShotObservation(string Rel, string? Issue, string? SourceIssue,
        string BoundsHash, string RendererHash, string Fingerprint);

    static NifMeshBuilder.BuiltMesh Triangle() => new()
    {
        Positions = [new(-10, 10, -10), new(10, 10, -10), new(0, 10, 10)],
        Normals = [-Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY],
        Indices = [0, 1, 2],
        TextureCoordinates = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
        Tangents = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX],
        Bitangents = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        VertexColors = [Vector4.One, Vector4.One, Vector4.One],
        ShapeName = "mesh-red-triangle",
        TexturePaths = new() { [0] = "", [1] = "" },
        HasAlphaTest = true, AlphaThreshold = .25f, HasAlphaBlend = false,
        MaterialAlpha = 1f, ZBufferWrite = true, IsDecal = true,
        IsDoubleSided = true, IsModelSpaceNormals = true, SrcBlendIndex = 6, DstBlendIndex = 7,
        HasEffectShader = false, EffectBaseColor = new(.1f, .2f, .3f),
        EffectBaseColorAlpha = .4f, EffectBaseColorScale = .5f,
        EmissiveColor = new(.6f, .7f, .8f), EmissiveMultiple = .9f,
    };

    static string Fingerprint(IReadOnlyList<NifMeshBuilder.BuiltMesh>? meshes)
    {
        if (meshes is null) return "null";
        var s = new StringBuilder();
        foreach (var b in meshes)
        {
            void A<T>(IEnumerable<T>? xs) => s.Append(xs is null ? "<null>" : string.Join(";", xs)).Append('|');
            s.Append(b.ShapeName).Append('|'); A(b.Positions); A(b.Normals); A(b.Indices);
            A(b.TextureCoordinates); A(b.Tangents); A(b.Bitangents); A(b.VertexColors);
            A(b.TexturePaths.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));
            s.AppendJoin('|', b.HasEffectShader, b.EffectBaseColor, b.EffectBaseColorAlpha,
                b.EffectBaseColorScale, b.EmissiveColor, b.EmissiveMultiple, b.HasAlphaTest,
                b.HasAlphaBlend, b.AlphaThreshold, b.MaterialAlpha, b.ZBufferWrite, b.IsDecal,
                b.IsDoubleSided, b.IsModelSpaceNormals, b.SrcBlendIndex, b.DstBlendIndex).Append('|');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToString())));
    }

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        string roots = Path.Combine(outDir, "roots");
        Directory.CreateDirectory(Path.Combine(roots, "data"));
        Directory.CreateDirectory(Path.Combine(roots, "mods"));
        Directory.CreateDirectory(Path.Combine(roots, "overwrite"));
        using var assets = AssetResolver.Build(Path.Combine(roots, "overwrite"), Path.Combine(roots, "mods"),
            Path.Combine(roots, "data"), Array.Empty<string>(), Array.Empty<ActiveArchive>());

        var nws = new NativeWindowSettings { ClientSize = new OpenTK.Mathematics.Vector2i(32, 32), StartVisible = false,
            StartFocused = false, WindowBorder = WindowBorder.Hidden, APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core };
        using var gw = new GameWindow(GameWindowSettings.Default, nws);
        gw.MakeCurrent();

        var refs = new[] { new SceneRef("meshes/fake.nif", new(0, 0, 0), new(0, 0, 0), 1f, false) };
        int shotNo = 0;
        int Render(string tag, TexturedScene.MeshReadHooks hooks, CellScene? cell, float? cut, bool occlusion,
            Vector3? eye = null)
        {
            UnreadableAssets.BeginShot();
            shotNo++;
            return TexturedScene.RenderToPng(refs, assets, Path.Combine(roots, "data"), Array.Empty<string>(),
                Path.Combine(roots, "overwrite"), (eye ?? new Vector3(0, -30, 10), new Vector3(0, 0, 10), 65),
                Array.Empty<(Vector3, Vector3, Vector3)>(), Array.Empty<(Vector3, Vector3, Vector3)>(),
                new Dictionary<string, List<(Vector3, Vector3, Vector3)>>(),
                Array.Empty<(string, List<(Vector3, Vector3, Vector3, float, float, float)>)>(),
                "off", "auto", new(.2f, .3f, .4f), null, false, false, 0, cut, null,
                Array.Empty<Vector3>(), Vector4.One, Array.Empty<(int, int)>(), Array.Empty<string>(),
                Array.Empty<(string, Vector3)>(), Array.Empty<NpcActorRender>(), false,
                Array.Empty<TexturedScene.RouteRender>(), null, Array.Empty<TexturedScene.OverlayLine>(), false, false, false,
                Array.Empty<WaterPlane>(), new Dictionary<Mutagen.Bethesda.Plugins.FormKey, Covering>(),
                Array.Empty<PlaneRender>(), false, "off", 32, Path.Combine(outDir, $"{tag}-{shotNo}.png"),
                planeReport: occlusion ? _ => { } : null, cellScene: cell, meshReadHooks: hooks);
        }

        static TexturedScene.MeshReadHooks Hooks(Func<List<NifMeshBuilder.BuiltMesh>> build,
            Func<int, byte[]> bytes, List<ShotObservation> seen, Action? onBuild = null,
            Action? onMeshStateFactory = null)
        {
            int reads = 0;
            return new TexturedScene.MeshReadHooks
            {
                Resolve = _ => "mesh-red-fake.nif",
                Build = _ => { onBuild?.Invoke(); return build(); },
                ReadBytes = _ => bytes(++reads),
                Observe = (rel, built, issue, sourceIssue, boundsHash, rendererHash) =>
                    seen.Add(new(rel, issue, sourceIssue, boundsHash, rendererHash, Fingerprint(built))),
                ObserveMeshStateFactory = onMeshStateFactory,
            };
        }

        var verdict = new Dictionary<string, bool>();

        static string Pixels(string path)
        {
            using var image = Image.Load<Rgba32>(path);
            var bytes = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(bytes);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        int offBuilds = 0, offStateFactories = 0, cutTriangles = -1, cutShapes = -1; var offSeen = new List<ShotObservation>();
        var offBaseHooks = Hooks(() => [Triangle()], _ => [1, 2, 3], offSeen,
            () => offBuilds++, () => offStateFactories++);
        var offHooks = new TexturedScene.MeshReadHooks
        {
            Resolve = offBaseHooks.Resolve, Build = offBaseHooks.Build, ReadBytes = offBaseHooks.ReadBytes,
            Observe = offBaseHooks.Observe,
            ObserveCull = (triangles, shapes) => { cutTriangles = triangles; cutShapes = shapes; },
            ObserveMeshStateFactory = offBaseHooks.ObserveMeshStateFactory
        };
        var cutEye = new Vector3(0, -30, 30);
        int offUncutShapes = Render("off-a", offHooks, null, null, false, cutEye);
        string offUncutPixels = Pixels(Path.Combine(outDir, "off-a-1.png"));
        int offCutShapes = Render("off-b", offHooks, null, 0, false, cutEye);
        string offCutPixels = Pixels(Path.Combine(outDir, "off-b-2.png"));
        verdict["reuseOffBuildsPerShotAlreadyGreen"] = offBuilds == 2;
        verdict["reuseOffMeshStateFactoryPerShotAlreadyGreen"] = offStateFactories == 2;
        verdict["geometryChangingCutAlreadyGreen"] = offUncutShapes > 0 && cutTriangles > 0 && cutShapes > 0
            && offUncutPixels != offCutPixels;

        int onBuilds = 0, onStateFactories = 0; var onSeen = new List<ShotObservation>();
        var sharedBuilt = new List<NifMeshBuilder.BuiltMesh> { Triangle() };
        string sharedBefore = Fingerprint(sharedBuilt);
        using (var cell = new CellScene("MeshRedCell"))
        {
            var onHooks = Hooks(() => sharedBuilt, _ => [1, 2, 3], onSeen,
                () => onBuilds++, () => onStateFactories++);
            Render("on-a", onHooks, cell, null, false);
            Render("on-b", onHooks, cell, null, false, new Vector3(15, -35, 15));
        }
        using (var nextCell = new CellScene("MeshRedNextCell"))
        {
            var nextHooks = Hooks(() => sharedBuilt, _ => [1, 2, 3], new(),
                () => onBuilds++, () => onStateFactories++);
            Render("on-next-cell", nextHooks, nextCell, null, false);
        }
        verdict["meshRetainedWithinCellAndRebuiltNextCell"] = onBuilds == 2;
        verdict["meshStateFactoryOncePerCell"] = onStateFactories == 2;
        verdict["fingerprintStableAcrossCameraCutAlreadyGreen"] = onSeen.Count == 2 && onSeen[0].Fingerprint == onSeen[1].Fingerprint;
        verdict["consumedObjectRemainsImmutableAlreadyGreen"] = sharedBefore == Fingerprint(sharedBuilt);

        foreach (var (name, factory) in new (string, Func<List<NifMeshBuilder.BuiltMesh>>)[]
        {
            ("null", () => null!), ("empty", () => []),
        })
        {
            int builds = 0; var seen = new List<ShotObservation>();
            var hooks = Hooks(factory, _ => [4, 5, 6], seen, () => builds++);
            using var cell = new CellScene("Outcome-" + name);
            Render(name + "-a", hooks, cell, null, false); Render(name + "-b", hooks, cell, null, false);
            verdict[$"{name}OutcomeRetainedOnce"] = builds == 1;
            verdict[$"{name}OutcomeProjectedBothShotsAlreadyGreen"] = seen.Count == 2;
        }

        int missingResolves = 0; var missingSeen = new List<ShotObservation>();
        var missingHooks = new TexturedScene.MeshReadHooks
        {
            Resolve = _ => { missingResolves++; return null; },
            Build = _ => throw new InvalidOperationException("missing path must not build"),
            ReadBytes = _ => throw new InvalidOperationException("missing path must not read"),
            Observe = (rel, built, issue, sourceIssue, boundsHash, rendererHash) =>
                missingSeen.Add(new(rel, issue, sourceIssue, boundsHash, rendererHash, Fingerprint(built)))
        };
        using (var cell = new CellScene("Missing"))
        { Render("missing-a", missingHooks, cell, null, false); Render("missing-b", missingHooks, cell, null, false); }
        verdict["missingPathOutcomeRetainedOnce"] = missingResolves == 3 && missingSeen.Count == 2;

        int unreadableBuilds = 0; var unreadableSeen = new List<ShotObservation>();
        var unreadableHooks = Hooks(() => [Triangle()], _ => throw new IOException("identity unavailable"),
            unreadableSeen, () => unreadableBuilds++);
        using (var cell = new CellScene("UnreadableIdentity"))
        { Render("unreadable-off", unreadableHooks, cell, null, false); Render("unreadable-on", unreadableHooks, cell, null, true); }
        verdict["identityUnavailableReportedAndRebuiltAlreadyGreen"] = unreadableBuilds == 2
            && unreadableSeen.Count == 2 && unreadableSeen.All(x => x.BoundsHash.Length == 0)
            && unreadableSeen[1].SourceIssue?.Contains("identity unavailable") == true;

        int failBuilds = 0; var failSeen = new List<ShotObservation>();
        var failHooks = Hooks(() => throw new InvalidDataException("MESH_RED_FAILURE"), _ => [7], failSeen, () => failBuilds++);
        var oldErr = Console.Error; var err = new StringWriter(); Console.SetError(err);
        try { using var cell = new CellScene("Failure"); Render("fail-a", failHooks, cell, null, false); Render("fail-b", failHooks, cell, null, false); }
        finally { Console.SetError(oldErr); }
        verdict["failureRetainedOnce"] = failBuilds == 1;
        verdict["failureProjectedBothShotsAlreadyGreen"] = failSeen.Count == 2 && failSeen.All(x => x.Issue?.Contains("MESH_RED_FAILURE") == true);
        verdict["failureConsolePerShotAlreadyGreen"] = err.ToString().Split("MESH_RED_FAILURE").Length - 1 == 2;

        int hashBuilds = 0, hashReads = 0; var hashSeen = new List<ShotObservation>();
        var hashHooks = Hooks(() => [Triangle()], n => { hashReads++; return n % 2 == 0 ? [9, 9] : [8, 8]; }, hashSeen, () => hashBuilds++);
        int readsAfterOff;
        using (var cell = new CellScene("Hash"))
        {
            Render("hash-off", hashHooks, cell, null, false);
            readsAfterOff = hashReads;
            Render("hash-on", hashHooks, cell, null, true);
        }
        string firstDigest = Convert.ToHexString(SHA256.HashData(new byte[] { 8, 8 }));
        verdict["rendererIdentityCapturedOnFirstOffMiss"] = readsAfterOff == 1
            && hashSeen[0].RendererHash == firstDigest && hashSeen[0].BoundsHash.Length == 0;
        verdict["offThenOnBuildsOnce"] = hashBuilds == 1;
        verdict["onShotRecomputesMismatchAlreadyGreen"] = hashSeen.Count == 2 && hashSeen[1].Issue?.Contains("changed between") == true;

        var matchSeen = new List<ShotObservation>();
        var matchHooks = Hooks(() => [Triangle()], _ => [5, 5], matchSeen);
        using (var cell = new CellScene("HashMatch")) { Render("hash-match", matchHooks, cell, null, true); }
        string matchDigest = Convert.ToHexString(SHA256.HashData(new byte[] { 5, 5 }));
        verdict["matchingDigestHasNoStaleMismatchAlreadyGreen"] = matchSeen.Count == 1
            && matchSeen[0].BoundsHash == matchDigest && matchSeen[0].Issue is null;

        int transitionRead = 0; var transitionSeen = new List<ShotObservation>();
        byte[][] transitionBytes = [[8, 8], [9, 9], [9, 9], [9, 9]];
        var transitionHooks = Hooks(() => [Triangle()], _ => transitionBytes[Math.Min(transitionRead++, 3)], transitionSeen);
        using (var cell = new CellScene("HashTransition"))
        {
            Render("hash-transition-mismatch", transitionHooks, cell, null, true);
            Render("hash-transition-match", transitionHooks, cell, null, true);
        }
        verdict["sameCellMismatchThenMatchClearsStaleIssueAlreadyGreen"] = transitionSeen.Count == 2
            && transitionSeen[0].Issue?.Contains("changed between") == true && transitionSeen[1].Issue is null;

        var mutation = Triangle(); string before = Fingerprint([mutation]); mutation.Positions[0].X += 1;
        verdict["positionMutationDetectedControl"] = before != Fingerprint([mutation]);
        mutation = Triangle(); before = Fingerprint([mutation]); mutation.Indices[0] = 2;
        verdict["indexMutationDetectedControl"] = before != Fingerprint([mutation]);
        mutation = Triangle(); before = Fingerprint([mutation]); mutation.TexturePaths[0] = "textures/changed.dds";
        verdict["materialMutationDetectedControl"] = before != Fingerprint([mutation]);

        var disposed = new CellScene("ForcedFailure");
        try
        {
            var exploding = Hooks(() => [Triangle()], _ => [1], new());
            exploding = new TexturedScene.MeshReadHooks
            {
                Resolve = exploding.Resolve, Build = exploding.Build, ReadBytes = exploding.ReadBytes,
                Observe = (_, _, _, _, _, _) => throw new InvalidOperationException("forced shot failure"),
                ObserveCull = exploding.ObserveCull,
                ObserveMeshStateFactory = exploding.ObserveMeshStateFactory
            };
            try { Render("forced-failure", exploding, disposed, null, false); }
            catch (InvalidOperationException e) when (e.Message == "forced shot failure") { }
        }
        finally { disposed.Dispose(); }
        bool disposedFactoryRan = false;
        try
        {
            _ = disposed.Mesh(() =>
            {
                disposedFactoryRan = true;
                return new MeshState(null!);
            });
            verdict["disposedCellRefusesMeshAcquisition"] = false;
        }
        catch (ObjectDisposedException)
        {
            verdict["disposedCellRefusesMeshAcquisition"] = true;
        }
        verdict["disposedMeshFactoryDidNotRun"] = !disposedFactoryRan;
        verdict["forcedFailureFinallyEndsCell"] = verdict["disposedCellRefusesMeshAcquisition"];

        var failed = verdict.Where(x => !x.Value).Select(x => x.Key).ToArray();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { notMet = failed,
            verdicts = verdict, counts = new { offBuilds, onBuilds, offStateFactories, onStateFactories,
                failBuilds, hashBuilds, hashReads },
            diagnostics = new
            {
                cut = new { offUncutShapes, offCutShapes, cutTriangles, cutShapes,
                    uncutPixelsSha256 = offUncutPixels, cutPixelsSha256 = offCutPixels },
                identity = new
                {
                    readsAfterOff,
                    firstOffBoundsSha256 = hashSeen[0].BoundsHash,
                    firstOffRendererSha256 = hashSeen[0].RendererHash,
                    onBoundsSha256 = hashSeen[1].BoundsHash,
                    onRendererSha256 = hashSeen[1].RendererHash,
                    onIssue = hashSeen[1].Issue,
                    unavailableOffSourceIssue = unreadableSeen[0].SourceIssue,
                    unavailableOnSourceIssue = unreadableSeen[1].SourceIssue,
                }
            },
            note = "ordinary selftest: exit 0 only when every retained-mesh contract holds" },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return failed.Length == 0 ? 0 : 1;
    }
}
