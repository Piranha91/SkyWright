using System.Numerics;
using CharacterViewer.Rendering;
using HousecarlCore;
using OpenTK.Graphics.OpenGL4;
using ReactiveUI.Builder;               // WithWpf()/BuildApp() are extension methods here

namespace ViewProbe;

// ======================================================================
//  NpcActor — hosts CharacterViewer.Rendering's FULL character pipeline
//  (VM_CharacterViewer: FaceGen head + CPU skinning to close the neck seam,
//  weight morphs, tints, slot hiding, outfit MeshOverrides) headless on
//  viewprobe's GL context, then BAKES each installed GlMesh from character-
//  local space into the Skyrim world placement so the probe's renderer can
//  draw the same mesh instances with the scene camera.
//
//  Hosting mirrors GameWindowOffscreenRenderer.LoadAndRender (the NPC2
//  mugshot path — the proof the VM runs with no WPF): InlineRenderThread-
//  Marshaller (the ctor default), IsOffscreenRenderInstance, InitializeGl,
//  LoadAsync + ProcessPendingSceneToCompletion, ApplyMeshOverrides. The VM's
//  own GlRenderer never renders — its Meshes are BORROWED into the probe's
//  renderer per panel and must outlive every panel, so the VMs are disposed
//  by Program.cs after the last render (disposal deletes the VAOs/textures).
//
//  Bake orientation: the builder outputs Y-up viewer space where the
//  character faces -Z (OrbitCamera.Azimuth doc), which un-swaps to Skyrim
//  +Y = NORTH — exactly the ref convention (heading 0 = north), so actors
//  compose through the SAME negated-Euler path as every static in
//  TexturedScene, with no facing fudge.
// ======================================================================

/// <summary>One placed actor, ready for TexturedScene: world-baked meshes
/// (borrowed — the scene must never dispose them) and the puck footprint.
/// PuckColor is the actor's IDENTITY colour — the puck fill, the rim, and the
/// caption line all wear it, so a multi-actor render says who is who.</summary>
sealed record NpcActorRender(
    IReadOnlyList<GlMesh> Meshes,
    Vector3 PositionSkyrim,             // feet (the ACHR/spec position)
    float PuckRadius,
    Vector3 PuckColor,
    string Label);

/// <summary>Placement for one actor, Skyrim space. Rotation is Euler XYZ
/// radians exactly as a PlacedRef carries it (actors effectively use Z).</summary>
readonly record struct NpcPlacement(Vector3 Position, Vec3 Rotation, float RefScale);

static class NpcActorBuilder
{
    /// <summary>Shared per-run pipeline state: one resolver + one parsed-NIF /
    /// decoded-DDS cache across all actors AND all panels. Build once after the
    /// GL context is current; dispose via <see cref="Disposer"/> after the last
    /// panel is saved.</summary>
    public sealed class Host
    {
        public required GameAssetResolver Resolver { get; init; }
        public required CharacterPreviewCache Cache { get; init; }
        public required CharacterViewerLogGate Gate { get; init; }
        public required ConsoleCvLogger Logger { get; init; }
        public required BodySlideDeformer Deformer { get; init; }
        public required BsdFileParser Bsd { get; init; }
        public required BodyTriFileParser Tri { get; init; }
        public List<VM_CharacterViewer> Vms { get; } = new();

        /// <summary>Dispose every VM (deletes their meshes' VAOs and textures).
        /// Only after the LAST panel has been rendered and saved.</summary>
        public void Disposer()
        {
            foreach (var vm in Vms)
            {
                try { vm.Dispose(); } catch { /* GL teardown on exit — never fatal */ }
            }
            Vms.Clear();
        }
    }

    static bool _rxInitialized;

    public static Host CreateHost(AssetResolver assets, string dataDir,
        IReadOnlyList<string> modFoldersHighestFirst, string overwriteDir)
    {
        // ReactiveUI 20+ no longer self-initializes on assembly load; the VM's
        // WhenAnyValue subscriptions throw without this. Core services only —
        // there is no WPF Application here — and an IMMEDIATE main-thread
        // scheduler so nothing the VM's reactive plumbing emits ever hops to a
        // pool thread (this whole pipeline is single-threaded on the GL thread).
        if (!_rxInitialized)
        {
            // .WithWpf() is the chain both host apps use (BuildApp hangs off the
            // WPF builder type); its lazy dispatcher scheduler never sticks
            // anyway and is overridden right below.
            RxAppBuilder.CreateReactiveUIBuilder()
                .WithWpf()
                .BuildApp();
            ReactiveUI.RxSchedulers.MainThreadScheduler = System.Reactive.Concurrency.ImmediateScheduler.Instance;
            _rxInitialized = true;
        }

        var logger = new ConsoleCvLogger();
        var gate = new CharacterViewerLogGate { Verbose = Environment.GetEnvironmentVariable("VP_CV_VERBOSE") == "1" };
        var resolver = new GameAssetResolver(new HcDataFolder(dataDir), new HcBsaProvider(assets), gate, logger);
        resolver.SetAdditionalFolders(TexturedScene.ResolverFolders(modFoldersHighestFirst, overwriteDir));
        var cache = new CharacterPreviewCache(new NullNpcSource(), resolver, logger, gate, new CvSettings());
        return new Host
        {
            Resolver = resolver,
            Cache = cache,
            Gate = gate,
            Logger = logger,
            Deformer = new BodySlideDeformer(logger, gate),
            Bsd = new BsdFileParser(logger, gate),
            Tri = new BodyTriFileParser(logger),
        };
    }

    /// <summary>Loads one NPC through the full character pipeline and bakes the
    /// installed meshes to the placement. GL context must be current. Returns
    /// null when nothing at all could be built (the caller reports and skips).</summary>
    public static NpcActorRender? Build(Host host, ResolvedNpc npc,
        IReadOnlyList<MeshOverride>? outfitOverrides, NpcPlacement placement, string label, Vector3 puckColor)
    {
        var vm = new VM_CharacterViewer(host.Deformer, host.Bsd, host.Tri, host.Resolver,
            new CvSettings(), host.Cache, host.Gate, host.Logger);
        host.Vms.Add(vm);                                  // disposal owns it from here, success or not
        vm.IsOffscreenRenderInstance = true;
        vm.InitializeGl(ModuleResourceLocator.ShaderDirectory);
        // White fallback, matching the probe's statics — a green wireframe
        // mannequin would read as a finding.
        vm.RenderMissingTextureAsWireframe = false;

        var identity = new NpcIdentity(npc.NpcKey.ToString(), label);
        try
        {
            vm.LoadAsync(identity, npc.Paths).GetAwaiter().GetResult();
            vm.ProcessPendingSceneToCompletion();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"   [npc] {label}: character build failed — {ex.Message}");
            return null;
        }

        if (vm.MissingMeshPaths.Count > 0)
            foreach (var mp in vm.MissingMeshPaths)
                Console.WriteLine($"   [npc] {label}: mesh NOT resolved: {mp}");

        if (outfitOverrides is { Count: > 0 })
        {
            try
            {
                vm.ApplyMeshOverrides(outfitOverrides);
                foreach (var w in vm.MeshOverrideWarnings)
                    Console.WriteLine($"   [npc] {label}: outfit: {w}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"   [npc] {label}: outfit apply failed — {ex.Message}");
            }
        }

        var meshes = vm.Renderer.Meshes.ToList();
        if (meshes.Count == 0)
        {
            Console.Error.WriteLine($"   [npc] {label}: character pipeline produced no shapes");
            return null;
        }

        float worldScale = placement.RefScale * npc.TotalHeight;
        BakeWorldPlacement(meshes, placement, worldScale);

        int hidden = meshes.Count(m => !m.ShouldRender);
        Console.WriteLine($"   [npc] {label}: {meshes.Count} shape(s) built"
                        + (hidden > 0 ? $" ({hidden} slot-hidden under the outfit)" : "")
                        + $", scale {worldScale:0.###} (ref {placement.RefScale:0.##} x npc {npc.TotalHeight:0.###})"
                        + (vm.MissingTexturePaths.Count > 0 ? $", {vm.MissingTexturePaths.Count} texture(s) unresolved" : ""));

        return new NpcActorRender(meshes, placement.Position, 34f * worldScale, puckColor, label);
    }

    /// <summary>Rewrites each mesh's VBO in place: character-local viewer space →
    /// Skyrim placement → back to viewer space. The EXACT static-ref convention
    /// from TexturedScene (Skyrim REFR angles are clockwise-positive compass
    /// headings, so they are NEGATED before composing with the right-handed
    /// SpatialOverlap.Euler). Layout is GlMesh's 18-float interleave:
    /// pos(0-2) normal(3-5) uv(6-7) colour(8-11) tangent(12-14) bitangent(15-17).</summary>
    static void BakeWorldPlacement(IReadOnlyList<GlMesh> meshes, NpcPlacement placement, float scale)
    {
        var rot = SpatialOverlap.Euler(
            new Vec3(-placement.Rotation.X, -placement.Rotation.Y, -placement.Rotation.Z),
            SpatialOverlap.EulerOrder.Xyz);
        var pos = placement.Position;

        const int Stride = 18;
        foreach (var mesh in meshes)
        {
            if (mesh.Vbo == 0) continue;
            GL.BindBuffer(BufferTarget.ArrayBuffer, mesh.Vbo);
            GL.GetBufferParameter(BufferTarget.ArrayBuffer, BufferParameterName.BufferSize, out int bytes);
            int vertCount = bytes / (Stride * sizeof(float));
            if (vertCount <= 0) continue;
            var data = new float[vertCount * Stride];
            GL.GetBufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, bytes, data);

            for (int i = 0; i < vertCount; i++)
            {
                int o = i * Stride;
                // Position: viewer Y-up -> Skyrim Z-up, scale, rotate, place, swap back.
                var zp = new Vector3(data[o], -data[o + 2], data[o + 1]);
                var wp = Vector3.Transform(zp * scale, rot) + pos;
                data[o] = wp.X; data[o + 1] = wp.Z; data[o + 2] = -wp.Y;

                void Dir(int at)
                {
                    var zd = new Vector3(data[at], -data[at + 2], data[at + 1]);
                    if (zd.LengthSquared() < 1e-12f) return;
                    var wd = Vector3.TransformNormal(zd, rot);
                    data[at] = wd.X; data[at + 1] = wd.Z; data[at + 2] = -wd.Y;
                }
                Dir(o + 3);     // normal
                Dir(o + 12);    // tangent
                Dir(o + 15);    // bitangent
            }

            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, bytes, data);

            // Keep the CPU-side copy in step: LocalCenter (the alpha-pass
            // distance sort) derives from these, and with the probe's global
            // model matrix at identity a world-space centroid is what it needs.
            if (mesh.CpuPositions is { } cpu && cpu.Length == vertCount)
            {
                for (int i = 0; i < vertCount; i++)
                {
                    int o = i * Stride;
                    cpu[i] = new Vector3(data[o], data[o + 1], data[o + 2]);
                }
            }
            mesh.InvalidateLocalCenter();   // it may have been computed pre-bake
        }
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }
}
