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

        resident!.BeginRenderPass();
        evidence?.ResidentEpochAdvanced(++epoch);
        if (scene is not null && !StringComparer.Ordinal.Equals(scene.Key, staticKey))
        {
            scene.Dispose();
            scene = null;
        }
        scene ??= new BuiltScene(staticKey, renderer!);
        var manifest = scene.TouchOrRefuse(resident);
        evidence?.ManifestValidated("before-static", manifest.Valid, manifest.ExpectedCount, manifest.FoundCount);
        if (!manifest.Valid)
            throw new InvalidOperationException("retained residency manifest refused before draw: " + manifest.Failure);
        scene.BeginBindingPass();
        return scene;
    }

    internal void CompleteStaticBindings(GraphicsReuseRed.ShotScope? evidence)
    {
        ThrowIfDisposed();
        var current = scene ?? throw new InvalidOperationException("static scene not initialized");
        current.CompleteBindingPass();
        var manifest = current.TouchOrRefuse(Resident);
        evidence?.ManifestValidated("after-static", manifest.Valid, manifest.ExpectedCount, manifest.FoundCount);
        if (!manifest.Valid)
            throw new InvalidOperationException("populated residency manifest refused before draw: " + manifest.Failure);
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
