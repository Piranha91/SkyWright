using CharacterViewer.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace ViewProbe;

/// <summary>Opt-in worklist-cell owner for one context and its retained static scene.</summary>
internal sealed class GraphicsCell : IDisposable
{
    readonly GraphicsReuseRed? red;
    readonly GraphicsReuseRed.ContextScope? contextEvidence;
    readonly GameWindow window;
    readonly int ownerThreadId = Environment.CurrentManagedThreadId;
    readonly long? testBudgetBytes;
    ResidentTextureCache? resident;
    GlTextureManager? installTextures;
    GlRenderer? renderer;
    BuiltScene? scene;
    long epoch;
    bool disposed;

    internal GameWindow Window { get { ThrowIfDisposed(); return window; } }
    internal GlRenderer Renderer => renderer ?? throw new InvalidOperationException("graphics owners not initialized");
    internal GlTextureManager InstallTextures => installTextures ?? throw new InvalidOperationException("graphics owners not initialized");
    internal ResidentTextureCache Resident => resident ?? throw new InvalidOperationException("graphics owners not initialized");
    internal BuiltScene Scene => scene ?? throw new InvalidOperationException("static scene not initialized");

    internal GraphicsCell(NativeWindowSettings settings, GraphicsReuseRed? red, long? testBudgetBytes = null)
    {
        this.red = red;
        this.testBudgetBytes = testBudgetBytes;
        contextEvidence = red?.BeginContext();
        try
        {
            window = new GameWindow(GameWindowSettings.Default, settings);
            contextEvidence?.BindCreatedContext(window.Context);
            window.MakeCurrent();
            contextEvidence?.MadeCurrent();
        }
        catch
        {
            try { window?.Dispose(); } catch { }
            try { contextEvidence?.Dispose(); } catch { }
            throw;
        }
    }

    internal void MakeCurrent()
    {
        ThrowIfDisposed();
        if (Environment.CurrentManagedThreadId != ownerThreadId)
            throw new InvalidOperationException(
                $"graphics cell thread changed: owner {ownerThreadId}, current {Environment.CurrentManagedThreadId}");
        window.MakeCurrent();
        if (string.IsNullOrWhiteSpace(GL.GetString(StringName.Version)))
            throw new InvalidOperationException("graphics cell failed to make its context current");
    }

    internal BuiltScene BeginShot(string staticKey, CharacterPreviewCache cache,
        ICharacterViewerLogger logger, GraphicsReuseRed.ShotScope? evidence)
    {
        MakeCurrent();
        bool created = resident is null;
        if (created)
        {
            resident = new ResidentTextureCache(logger, testBudgetBytes);
            installTextures = new GlTextureManager(cache, logger, resident);
            installTextures.Initialize();
            renderer = new GlRenderer();
            renderer.Initialize(null);
        }
        evidence?.ResidentOwnerReady(resident!, created);
        evidence?.TextureManagerReady(installTextures!, initializedNow: created, role: "install", cellLifetime: true);
        evidence?.RendererReady(renderer!, initializedNow: created);
        // Measurement only (G5 RED, Gate B8): poison retained mutable state before this shot's resets; inert without a RED session.
        if (scene is not null) red?.PoisonBeforeShot(renderer!, scene, installTextures!);
        // Measurement only (Gate B4, G2 RED): a requested residency mutation between shots; inert without a RED session.
        if (scene is not null) red?.MutateResidency("before-validation", resident!, installTextures!, scene);

        red?.BeforeRenderPass(resident!, scene);   // measurement only (G4 RED): the 16th-pass pressure; inert without a RED session
        resident!.BeginRenderPass();
        red?.AfterRenderPass(resident, scene);     // measurement only (G4 RED): what the repoll did
        evidence?.ResidentEpochAdvanced(++epoch);
        string? priorKey = scene?.Key;
        if (scene is not null && !StringComparer.Ordinal.Equals(scene.Key, staticKey))
        {
            red?.SceneReplacing(scene, staticKey, installTextures!.WhiteTexture);   // measurement only
            scene.Dispose();
            scene = null;
            // G2 F1 (Gate B4, W1): the install manager's private path map served the retired scene and must not
            // outlive it; after an eviction it would hand the replacement a deleted handle. ClearCache deletes only
            // the manager's own non-resident uploads (never its white texture) and empties the map, so the
            // replacement's loads go to the resident cache: a hit reuses the handle, an evicted path uploads again.
            var clearing = red?.InstallCacheClearing(installTextures);   // measurement only
            installTextures.ClearCache();
            red?.InstallCacheCleared(installTextures, clearing);   // measurement only
        }
        bool sceneCreated = scene is null;
        scene ??= new BuiltScene(staticKey, renderer!);
        if (sceneCreated) red?.ObserveScene(scene);   // measurement only: attaches the dispose record
        red?.StaticKeyDecision(staticKey, priorKey, sceneCreated, scene);   // measurement only
        var manifest = scene.TouchOrRefuse(resident);
        evidence?.ManifestValidated("before-static", manifest.Valid, manifest.ExpectedCount, manifest.FoundCount);
        if (!manifest.Valid)
            throw new InvalidOperationException("retained residency manifest refused before draw: " + manifest.Failure);
        if (!sceneCreated) red?.MutateResidency("after-before-static", resident, installTextures!, scene);   // measurement only
        red?.PressureAfterTouch(resident, scene);   // measurement only (G4 RED): pinned-budget pressure after the touch
        scene.BeginBindingPass();
        return scene;
    }

    internal void CompleteStaticBindings(GraphicsReuseRed.ShotScope? evidence)
    {
        ThrowIfDisposed();
        var current = scene ?? throw new InvalidOperationException("static scene not initialized");
        current.CompleteBindingPass();
        red?.ManifestSnapshot(evidence, "after-static", current, Resident, installTextures);   // measurement only
        var manifest = current.TouchOrRefuse(Resident);
        evidence?.ManifestValidated("after-static", manifest.Valid, manifest.ExpectedCount, manifest.FoundCount);
        if (!manifest.Valid)
            throw new InvalidOperationException("populated residency manifest refused before draw: " + manifest.Failure);
    }

    /// <summary>Measurement only (Gate B4, G2 RED): the retained manifest against the resident cache at each
    /// draw. Inert without a RED session.</summary>
    internal void ObserveDraw(GraphicsReuseRed.ShotScope? evidence)
    {
        if (red is null || evidence is null || scene is null || resident is null) return;
        red.DrawBoundary(evidence, scene, resident, installTextures);
        red.PreDrawState(renderer, scene, installTextures);   // measurement only (G5 RED)
    }

    internal void EndShot()
    {
        ThrowIfDisposed();
        scene?.RestoreBaseline();
    }

    public void Dispose()
    {
        if (disposed) return;
        List<Exception> failures = [];
        bool current = false;
        try { MakeCurrent(); current = true; }
        catch (Exception ex) { failures.Add(ex); }
        disposed = true;
        if (current)
        {
            DisposeOne(() => red?.G45CellEnd(), failures);   // measurement only (G4/G5 RED): the hooks' scratch objects
            // Measurement only (inert without a RED session): the resident cache holds exactly the retained manifest.
            DisposeOne(() => { if (resident is not null) red?.ResidentAtCellEnd(resident, scene?.ResidentDiskPaths ?? Array.Empty<string>()); }, failures);
            DisposeOne(() => scene?.Dispose(), failures); scene = null;
            DisposeOne(() => renderer?.Dispose(), failures); renderer = null;
            DisposeOne(() => installTextures?.Dispose(), failures); installTextures = null;
            DisposeOne(() => resident?.Dispose(), failures); resident = null;
            DisposeOne(() => red?.CellOwnersDisposedWhileContextCurrent(), failures);
            DisposeOne(() => window.Context.MakeNoneCurrent(), failures);
            DisposeOne(window.Dispose, failures);
            DisposeOne(() => contextEvidence?.Dispose(), failures);
        }
        else
        {
            failures.Add(new InvalidOperationException(
                "graphics owner deletion skipped because the creating thread/context could not be made current"));
        }
        if (failures.Count > 0) throw new AggregateException("graphics cell teardown failed", failures);
    }

    static void DisposeOne(Action action, List<Exception> failures)
    {
        try { action(); } catch (Exception ex) { failures.Add(ex); }
    }
    void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(GraphicsCell));
    }
}
