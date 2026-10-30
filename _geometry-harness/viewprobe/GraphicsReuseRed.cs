using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SkyRender;
using SkyRender.Character;
using OpenTK.Graphics.OpenGL4;

namespace ViewProbe;

/// <summary>Private opt-in observer for the real graphics reuse RED. It records completed real
/// operations and integer handles; weak object identities never extend an owner's lifetime.</summary>
internal sealed class GraphicsReuseRed : IDisposable
{
    const string OutputVariable = "VP_GRAPHICS_REUSE_RED_OUT";
    const string FaultVariable = "VP_GRAPHICS_REUSE_RED_FAULT";
    static readonly object StaticGate = new();
    static Session? active;

    readonly Session session;
    readonly CellState cell;
    ContextScope? currentContext;
    bool disposed;

    GraphicsReuseRed(Session session, CellState cell) { this.session = session; this.cell = cell; }

    public static bool TestBoundaryEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OutputVariable));

    public static GraphicsReuseRed? TryBeginCell(string label, int index)
    {
        string? output = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(output)) return null;
        output = Path.GetFullPath(output);
        lock (StaticGate)
        {
            active ??= new Session(output, Environment.GetEnvironmentVariable(FaultVariable));
            if (!StringComparer.OrdinalIgnoreCase.Equals(active.OutputPath, output))
                throw new InvalidOperationException("graphics RED output changed during one process");
            return new(active, active.BeginCell(label, index));
        }
    }

    /// <summary>Declare before the real GameWindow using declaration. The scope's Dispose then
    /// records post-window owner order only; it deliberately performs no GL query.</summary>
    public ContextScope BeginContext()
    {
        ThrowIfDisposed();
        if (currentContext is not null) throw new InvalidOperationException("graphics context scopes overlap");
        currentContext = new(this, session.NextId("context-scope"));
        session.Emit("context-scope-begin", cell, null, new { currentContext.ScopeId });
        return currentContext;
    }

    internal ShotScope BeginShot(GraphicsShotFacts facts)
    {
        ThrowIfDisposed();
        if (currentContext is null || !currentContext.IsCurrent || currentContext.ContextId is null)
            throw new InvalidOperationException("graphics shot began without a measured current context");
        var shot = cell.BeginShot(session.NextId("shot"), currentContext.ContextId, facts);
        currentContext.ShotCount++;
        session.Emit("shot-begin", cell, shot, facts);
        return new(session, cell, shot);
    }

    /// <summary>G3 RED (launch 8): borrowed NPC meshes just before their owning VMs are disposed. Returns the handle
    /// snapshot the after-call checks (GlMesh.Dispose keeps its integer handles; nothing allocates in between).</summary>
    public IReadOnlyList<(GlMesh Mesh, int Vao, int Vbo, int Ebo)> BorrowedOwnersDisposing(IReadOnlyList<GlMesh> meshes, GlRenderer? renderer)
    {
        ThrowIfDisposed();
        var snapshot = meshes.Select(m => (m, m.Vao, m.Vbo, m.Ebo)).ToArray();
        BorrowedOwners("borrowed-owner-before-dispose", snapshot, renderer);
        return snapshot;
    }

    public void BorrowedOwnersDisposed(IReadOnlyList<(GlMesh Mesh, int Vao, int Vbo, int Ebo)> snapshot, GlRenderer? renderer)
    {
        ThrowIfDisposed();
        BorrowedOwners("borrowed-owner-after-dispose", snapshot, renderer);
    }

    void BorrowedOwners(string kind, IReadOnlyList<(GlMesh Mesh, int Vao, int Vbo, int Ebo)> snapshot, GlRenderer? renderer)
    {
        string? version = GL.GetString(StringName.Version);
        var rows = snapshot.Select(x => new
        {
            meshId = session.ObjectId(x.Mesh, "mesh"), x.Vao, x.Vbo, x.Ebo,
            vaoLive = x.Vao > 0 && GL.IsVertexArray(x.Vao), vboLive = x.Vbo > 0 && GL.IsBuffer(x.Vbo),
            eboLive = x.Ebo > 0 && GL.IsBuffer(x.Ebo), inRenderer = renderer?.Meshes.Contains(x.Mesh)
        }).ToArray();
        session.Emit(kind, cell, null, new
        {
            count = rows.Length, order = session.OrderMark(), contextCurrent = !string.IsNullOrWhiteSpace(version),
            rendererId = renderer is null ? null : session.ObjectId(renderer, "renderer"), rows
        });
    }

    /// <summary>GREEN owner calls this at cell end, before the scene and resident cache are disposed (Gate B2 review,
    /// C2 as corrected): the cache must hold exactly the retained manifest's distinct resident disk paths and no
    /// shot-local texture. TryGet's recency side effect is harmless here; the cache is disposed next.</summary>
    public void ResidentAtCellEnd(ResidentTextureCache resident, IReadOnlyCollection<string> manifestResidentPaths)
    {
        ThrowIfDisposed();
        // G2 (Gate B4): the residency hook's displaced originals are deleted first, so its own substitution does
        // not read as a leak in the cell teardown checks.
        foreach (int orphan in cell.OrphanHandles)
            if (orphan > 0 && GL.IsTexture(orphan)) GL.DeleteTexture(orphan);
        int count = resident.Count;
        var distinct = manifestResidentPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        int found = distinct.Count(p => resident.TryGet(p) > 0);
        var shotLocal = cell.ShotLocalDiskPaths.Where(p => !distinct.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();
        var shotLocalInCache = shotLocal.Where(p => resident.TryGet(p) > 0).ToArray();
        cell.ResidentAtCellEnd = new(count, distinct.Length, found, shotLocal.Length, shotLocalInCache,
            count == distinct.Length && found == distinct.Length && shotLocalInCache.Length == 0);
        session.Emit("resident-at-cell-end", cell, null, cell.ResidentAtCellEnd);
        // G2 (Gate B4) replacement-aware statement, beside the unchanged legacy one: the cache holds the current
        // manifest's distinct paths, all found, no shot-local path, and a surplus that is exactly the retired
        // scenes' paths still resident; retired paths the budget evicted are listed, not treated as errors.
        var retiredOnly = cell.RetiredResidentPaths.Where(p => !distinct.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();
        var retiredFound = retiredOnly.Where(p => resident.TryGet(p) > 0).ToArray();
        var retiredEvicted = retiredOnly.Except(retiredFound, StringComparer.OrdinalIgnoreCase).ToArray();
        cell.ResidentAtCellEndG2 = new(count, distinct.Length, found, retiredOnly.Length, retiredFound.Length,
            retiredEvicted, count - distinct.Length, shotLocalInCache,
            count == distinct.Length + retiredFound.Length && found == distinct.Length && shotLocalInCache.Length == 0);
        session.Emit("resident-at-cell-end-g2", cell, null, cell.ResidentAtCellEndG2);
    }

    /// <summary>GREEN owner calls this after disposing cell resources and before releasing its
    /// still-current context. Baseline has no such boundary, so the measured count remains zero.</summary>
    public void CellOwnersDisposedWhileContextCurrent()
    {
        ThrowIfDisposed();
        if (currentContext?.ContextId is not { } contextId || !currentContext.IsCurrent)
            throw new InvalidOperationException("cell teardown was not observed with its context current");
        string? version = GL.GetString(StringName.Version);
        bool current = !string.IsNullOrWhiteSpace(version);
        // G2 F2: retired resources left the survival set at replacement; teardown still checks every one of them.
        var checks = cell.CellResources.Concat(cell.RetiredResources).Where(r => r.ContextId == contextId)
            .GroupBy(r => r.Key).Select(g => g.First())
            .Select(r => ResourceCheck.For(r, current)).ToArray();
        cell.CellTeardownChecks.AddRange(checks);
        cell.CellTeardownContextCurrent = current;
        session.Emit("cell-resources-after-owner-dispose", cell, null,
            new { contextId, contextCurrent = current, glVersion = version, checks });
        // G3 (Gate B6 review, Q4): every borrowed NPC mesh this cell saw, at cell end with the context current:
        // disposed by its owner (GlMesh.IsDisposed) and its recorded handles' liveness.
        var borrowedRows = cell.BorrowedMeshes.GroupBy(b => b.Id).Select(g => g.First()).Select(b => new
        {
            meshId = b.Id, b.Vao, b.Vbo, b.Ebo,
            disposed = (bool?)b.Mesh.IsDisposed,
            vaoLive = current && b.Vao > 0 && GL.IsVertexArray(b.Vao),
            vboLive = current && b.Vbo > 0 && GL.IsBuffer(b.Vbo),
            eboLive = current && b.Ebo > 0 && GL.IsBuffer(b.Ebo)
        }).ToArray();
        session.Emit("borrowed-at-cell-end", cell, null, new
        {
            contextCurrent = current, reflectionFound = true, // GlMesh.IsDisposed is public now; key kept for the evidence schema count = borrowedRows.Length,
            disposedCount = borrowedRows.Count(r => r.disposed == true), rows = borrowedRows
        });
    }

    // ---- Gate B4, G2 RED: keys, replacement and residency. Measurement only; every call site is `red?.` or
    // `evidence?.`, so none of this runs without VP_GRAPHICS_REUSE_RED_OUT. ----

    /// <summary>Attach the dispose record to a newly created retained scene.</summary>
    internal void ObserveScene(BuiltScene scene)
    {
        ThrowIfDisposed();
        string sceneId = session.ObjectId(scene, "scene");
        scene.DisposeObserved = SceneDisposed;
        session.Emit("scene-created", cell, cell.Shots.LastOrDefault(), new { sceneId, key = scene.Key });
    }

    /// <summary>Runs at the end of BuiltScene.Dispose, after its deletions and before anything else is created:
    /// every mesh and owned-texture handle the scene held is queried now, so a reused GL name cannot hide a leak.</summary>
    void SceneDisposed(BuiltScene scene, IReadOnlyList<(string Category, int Vao, int Vbo, int Ebo)> meshes,
        IReadOnlyList<(string Category, int Handle)> owned)
    {
        string sceneId = session.ObjectId(scene, "scene");
        var liveMeshes = meshes.Where(m => (m.Vao > 0 && GL.IsVertexArray(m.Vao)) || (m.Vbo > 0 && GL.IsBuffer(m.Vbo))
            || (m.Ebo > 0 && GL.IsBuffer(m.Ebo))).ToArray();
        var liveOwned = owned.Where(o => o.Handle > 0 && GL.IsTexture(o.Handle)).ToArray();
        var shot = cell.Shots.LastOrDefault();
        var row = new SceneDisposal(sceneId, scene.Key, shot?.ShotId, session.OrderMark(), meshes.Count, owned.Count,
            liveMeshes.Length, liveOwned.Length, liveMeshes.Length == 0 && liveOwned.Length == 0);
        cell.SceneDisposals.Add(row);
        session.Emit("scene-disposed", cell, shot, new
        {
            row,
            liveMeshes = liveMeshes.Take(20).Select(m => new { m.Category, m.Vao, m.Vbo, m.Ebo }).ToArray(),
            liveOwned = liveOwned.Select(o => new { o.Category, o.Handle }).ToArray()
        });
    }

    /// <summary>GraphicsCell calls this immediately before disposing a scene whose key no longer matches.</summary>
    /// <summary>G2 F2 (Gate B4, W2): the survival set follows the retained scene, not every integer handle the cell
    /// ever tracked. The retired scene's meshes, owned textures and manifest textures (except the install manager's
    /// white texture, which stays) leave the survival set here, before any new upload can reuse their GL names;
    /// they are proven dead by the dispose record, and every one of them is checked again at cell teardown.</summary>
    internal void SceneReplacing(BuiltScene old, string newKey, int installWhite)
    {
        ThrowIfDisposed();
        var retired = old.ResidentDiskPaths.ToArray();
        cell.RetiredResidentPaths.UnionWith(retired);
        var meshes = old.MeshHandles;
        var owned = old.OwnedTextureHandles;
        bool allLive = meshes.All(m => GL.IsVertexArray(m.Vao) && GL.IsBuffer(m.Vbo) && GL.IsBuffer(m.Ebo))
            && owned.All(o => GL.IsTexture(o.Handle));
        var handles = new HashSet<(string Kind, int Handle)>();
        foreach (var m in meshes) { handles.Add(("vertex-array", m.Vao)); handles.Add(("buffer", m.Vbo)); handles.Add(("buffer", m.Ebo)); }
        foreach (var o in owned) handles.Add(("texture", o.Handle));
        foreach (var t in old.TextureManifest) if (t.Handle > 0 && t.Handle != installWhite) handles.Add(("texture", t.Handle));
        var moving = cell.CellResources.Where(r => handles.Contains((r.Kind, r.Handle))).ToArray();
        foreach (var r in moving) cell.CellResources.Remove(r);
        foreach (var r in moving) if (!cell.RetiredResources.Any(x => x.Key == r.Key)) cell.RetiredResources.Add(r);
        var shot = cell.Shots.LastOrDefault();
        var row = new SceneReplacement(session.ObjectId(old, "scene"), old.Key, newKey, shot?.ShotId, session.OrderMark(),
            meshes.Count, owned.Count, retired.Length, allLive, moving.Length);
        cell.Replacements.Add(row);
        session.Emit("scene-replacing", cell, shot, row);
    }

    /// <summary>G2 F1 measurement, before the install manager's ClearCache on a retirement.</summary>
    internal ClearProbe InstallCacheClearing(GlTextureManager manager)
    {
        ThrowIfDisposed();
        var map = manager.CachedTexturePaths;
        var all = manager.OwnedTextureHandles;
        int white = manager.WhiteTexture;
        return new(white, white > 0 && GL.IsTexture(white), map.Count, map.ToArray(),
            manager.MissingTexturePaths.Count, all.Count(h => h != white), session.OrderMark());
    }

    /// <summary>G2 F1 post-conditions, measured right after ClearCache: the white texture is the same live texture,
    /// the private path map is empty and a non-promoting peek of every path it held misses, and the missing-path
    /// set is unchanged in count.</summary>
    internal void InstallCacheCleared(GlTextureManager manager, ClearProbe? before)
    {
        ThrowIfDisposed();
        if (before is null) return;
        var map = manager.CachedTexturePaths;
        var all = manager.OwnedTextureHandles;
        int white = manager.WhiteTexture;
        bool whiteLive = white > 0 && GL.IsTexture(white);
        int peekHits = before.MappedPaths.Count(p => PeekPrivate(manager, p) != -1);
        int missingAfter = manager.MissingTexturePaths.Count;
        int ownedAfter = all.Count(h => h != white);
        var shot = cell.Shots.LastOrDefault();
        var row = new CacheClear(shot?.ShotId, before.White, white, before.WhiteLive, whiteLive, before.MapCount, map.Count,
            before.MappedPaths.Length, peekHits, before.MissingCount, missingAfter, before.OwnedNonWhite, ownedAfter,
            before.Order, session.OrderMark(),
            white == before.White && whiteLive && map.Count == 0 && peekHits == 0 && missingAfter == before.MissingCount && ownedAfter == 0);
        cell.CacheClears.Add(row);
        session.Emit("install-cache-cleared", cell, shot, row);
    }

    internal void StaticKeyDecision(string key, string? priorKey, bool sceneCreated, BuiltScene scene)
    {
        ThrowIfDisposed();
        string decision = priorKey is null ? "created" : sceneCreated ? "replaced" : "reused";
        var shot = cell.Shots.LastOrDefault();
        var row = new KeyDecision(key, priorKey, decision, session.ObjectId(scene, "scene"), session.OrderMark());
        shot?.KeyDecisions.Add(row);
        session.Emit("static-key-decision", cell, shot, row);
    }

    /// <summary>The requested residency mutation (VP_GRAPHICS_REUSE_RED_RESIDENCY), once per session, at its phase:
    /// delete | replace | evict before the next shot's validation, or replace-after-before-static after that
    /// shot's first validation passed. The target is the retained manifest's first terrain diffuse, which every
    /// static key of the fixture cell loads again.</summary>
    internal void MutateResidency(string phase, ResidentTextureCache resident, GlTextureManager manager, BuiltScene scene)
    {
        ThrowIfDisposed();
        string? mode = session.ResidencyMode;
        if (mode is null || session.ResidencyFired) return;
        string wanted = mode == "replace-after-before-static" ? "after-before-static" : "before-validation";
        if (!StringComparer.Ordinal.Equals(phase, wanted)) return;
        var manifest = scene.TextureManifest.Where(x => x.Resident && !string.IsNullOrEmpty(x.DiskPath)).ToArray();
        if (manifest.Length == 0) throw new InvalidOperationException("G2 residency mutation found no resident manifest entry");
        var target = manifest.FirstOrDefault(x => x.Category == "static-terrain-diffuse");
        if (target.DiskPath is null) target = manifest[0];
        session.ResidencyFired = true;
        string? entryId = ResidentEntryId(resident, target.DiskPath);
        int peekBefore = PeekResident(resident, target.DiskPath);
        int privateBefore = PeekPrivate(manager, target.GamePath);
        bool liveBefore = target.Handle > 0 && GL.IsTexture(target.Handle);
        int newHandle = 0;
        switch (mode)
        {
            case "delete":
                GL.DeleteTexture(target.Handle);
                break;
            case "replace":
            case "replace-after-before-static":
                newHandle = CreateSubstituteTexture();
                SetResidentHandle(resident, target.DiskPath!, newHandle);
                cell.OrphanHandles.Add(target.Handle);   // the displaced original stays live until cell end
                break;
            case "evict":
                EvictResident(resident, target.DiskPath!);
                GL.DeleteTexture(target.Handle);
                break;
        }
        var shot = cell.Shots.LastOrDefault();
        var row = new ResidencyMutation(mode, phase, target.Category, target.GamePath, target.DiskPath!, target.Handle, newHandle,
            peekBefore, PeekResident(resident, target.DiskPath), privateBefore, PeekPrivate(manager, target.GamePath),
            liveBefore, target.Handle > 0 && GL.IsTexture(target.Handle), newHandle > 0 && GL.IsTexture(newHandle),
            session.OrderMark(), entryId);
        cell.Mutations.Add(row);
        shot?.ResidencyMutations.Add(row);
        session.Emit("residency-mutated", cell, shot, row);
    }

    /// <summary>Every resident manifest entry against a non-promoting read of the resident cache and of the
    /// install manager's private path cache, just before the after-static validation.</summary>
    internal void ManifestSnapshot(ShotScope? evidence, string phase, BuiltScene scene, ResidentTextureCache resident,
        GlTextureManager? manager)
    {
        ThrowIfDisposed();
        var shot = cell.Shots.LastOrDefault();
        var row = CheckManifest(phase, 0, scene, resident, manager);
        shot?.ManifestChecks.Add(row);
        session.Emit("manifest-check", cell, shot, row);
        // G2 F1 measurement: after an eviction, the evicted path's binding in the scene now being validated, against
        // the resident cache (a new entry object means a new upload was added; the GL name itself may be reused).
        foreach (var m in cell.Mutations.Where(x => x.Mode == "evict"))
        {
            var e = scene.TextureManifest.FirstOrDefault(x => StringComparer.OrdinalIgnoreCase.Equals(x.DiskPath, m.DiskPath));
            string? now = ResidentEntryId(resident, m.DiskPath);
            int peek = PeekResident(resident, m.DiskPath);
            var check = new ReuploadCheck(shot?.ShotId, m.DiskPath, m.GamePath, m.Handle, e.DiskPath is null ? null : e.Handle,
                peek, e.DiskPath is not null && e.Handle > 0 && GL.IsTexture(e.Handle), m.EntryId, now,
                manager is null ? -1 : PeekPrivate(manager, m.GamePath),
                e.DiskPath is not null && now is not null && now != m.EntryId && peek == e.Handle && e.Handle > 0 && GL.IsTexture(e.Handle));
            cell.Reuploads.Add(check);
            session.Emit("evicted-path-rebound", cell, shot, check);
        }
    }

    /// <summary>The same check at each draw: a draw that begins with a manifest entry the resident cache does not
    /// own, or with a dead handle, is the failure G2's refusal must prevent.</summary>
    internal void DrawBoundary(ShotScope evidence, BuiltScene scene, ResidentTextureCache resident, GlTextureManager? manager)
    {
        ThrowIfDisposed();
        var shot = cell.Shots.LastOrDefault();
        int index = shot?.ManifestChecks.Count(x => x.Phase == "draw") ?? 0;
        var row = CheckManifest("draw", index, scene, resident, manager);
        shot?.ManifestChecks.Add(row);
        session.Emit("manifest-check", cell, shot, row);
    }

    ManifestCheck CheckManifest(string phase, int index, BuiltScene scene, ResidentTextureCache resident, GlTextureManager? manager)
    {
        var entries = scene.TextureManifest.Where(x => x.Resident).ToArray();
        var rows = new List<ManifestRow>();
        int mismatched = 0, dead = 0, privateStale = 0;
        foreach (var e in entries)
        {
            int peek = PeekResident(resident, e.DiskPath);
            int privatePeek = manager is null ? -1 : PeekPrivate(manager, e.GamePath);
            bool live = e.Handle > 0 && GL.IsTexture(e.Handle);
            bool mismatch = peek != e.Handle;
            bool stale = privatePeek == e.Handle && (mismatch || !live);
            if (mismatch) mismatched++;
            if (!live) dead++;
            if (stale) privateStale++;
            if ((mismatch || !live) && rows.Count < 50)
                rows.Add(new(e.Category, e.GamePath, e.DiskPath, e.Handle, peek, privatePeek, live));
        }
        return new(phase, index, session.OrderMark(), entries.Length, mismatched, dead, privateStale,
            mismatched == 0 && dead == 0, rows.ToArray());
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cell.Disposed = true;
        cell.CellFinallyOrder = session.OrderMark();
        session.Emit("cell-end", cell, null, new
        {
            cell.CellId,
            cellTeardownObserved = cell.CellTeardownContextCurrent is not null
        });
    }

    public static bool IsExpectedFault(Exception ex) => ex is ExpectedGraphicsFault;
    public static void CaughtExpectedFault(Exception ex)
    {
        lock (StaticGate)
        {
            if (active is null) return;
            active.ExpectedFaultCaught = true;
            try { active.Emit("expected-fault-caught", null, null, ExceptionBlock(ex)); }
            catch (Exception logEx) { Console.Error.WriteLine(logEx); }
        }
    }
    public static void CaughtUnexpected(Exception ex)
    {
        lock (StaticGate)
        {
            if (active is null) return;
            active.UnexpectedFaultCaught = true;
            try { active.Emit("unexpected-fault-caught", null, null, ExceptionBlock(ex)); }
            catch (Exception logEx) { Console.Error.WriteLine(logEx); }
        }
    }
    public static bool Finish()
    {
        lock (StaticGate)
        {
            try { return active?.Finish() ?? true; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return false; }
        }
    }

    static object ExceptionBlock(Exception ex) => new
    {
        type = ex.GetType().FullName, ex.Message, ex.StackTrace,
        inner = ex.InnerException is null ? null : ExceptionBlock(ex.InnerException)
    };

    void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(GraphicsReuseRed));
    }

    internal sealed record GraphicsShotFacts(
        string CameraSha256,
        int StaticRefPopulation,
        int TerrainPatchPopulation,
        int TerrainLayerPopulation,
        int WaterPopulation,
        int NavTrianglePopulation,
        int RedTrianglePopulation,
        int NpcPopulation,
        int NpcPuckPopulation,
        int RoutePopulation,
        int GridPopulation,
        int OverlayLinePopulation,
        int PlanePopulation,
        int SubjectMaskPopulation);

    internal sealed class ContextScope : IDisposable
    {
        readonly GraphicsReuseRed owner;
        bool disposed;
        internal string ScopeId { get; }
        internal string? ContextId { get; private set; }
        internal bool IsCurrent { get; private set; }
        internal int ShotCount { get; set; }

        internal ContextScope(GraphicsReuseRed owner, string scopeId) { this.owner = owner; ScopeId = scopeId; }

        /// <summary>Bind the actual GameWindow.Context object immediately after construction.</summary>
        public void BindCreatedContext(object context)
        {
            if (ContextId is not null) throw new InvalidOperationException("graphics context bound twice");
            ContextId = owner.session.ObjectId(context, "context");
            owner.cell.ContextIds.Add(ContextId);
            owner.session.Emit("context-created", owner.cell, null, new { ScopeId, ContextId });
        }

        public void MadeCurrent()
        {
            if (ContextId is null) throw new InvalidOperationException("MakeCurrent preceded context binding");
            string? version = GL.GetString(StringName.Version);
            IsCurrent = !string.IsNullOrWhiteSpace(version);
            owner.session.Emit("context-made-current", owner.cell, null,
                new { ScopeId, ContextId, glVersion = version, contextQuerySucceeded = IsCurrent });
            if (!IsCurrent) throw new InvalidOperationException("current GL context query failed");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Declared before GameWindow, this runs after GameWindow.Dispose. No GL call is legal here.
            owner.cell.ContextOwnerEndCount++;
            owner.cell.LastContextOwnerEndOrder = owner.session.OrderMark();
            owner.session.Emit("context-owner-end-after-window-dispose", owner.cell, null,
                new { ScopeId, ContextId, ShotCount, glQueryAttempted = false });
            owner.currentContext = null;
        }
    }

    internal sealed class ShotScope : IDisposable
    {
        readonly Session session;
        readonly CellState cell;
        readonly ShotState shot;
        GraphicsLifetime? lifetime;
        bool disposed;

        internal ShotScope(Session session, CellState cell, ShotState shot)
        { this.session = session; this.cell = cell; this.shot = shot; }

        public IDisposable BeginGraphicsLifetime()
        {
            if (lifetime is not null) throw new InvalidOperationException("graphics lifetime began twice");
            lifetime = new(session, cell, shot);
            session.Emit("graphics-lifetime-begin", cell, shot, new { });
            return lifetime;
        }

        /// <param name="initializedNow">True only immediately after the actual Initialize call.</param>
        public void TextureManagerReady(GlTextureManager manager, bool initializedNow, string role, bool cellLifetime)
        {
            string id = session.ObjectId(manager, "texture-manager");
            shot.TextureManagers.Add(new(id, role, initializedNow));
            Track("texture", role + ":white", manager.WhiteTexture,
                cellLifetime ? Lifetime.Cell : Lifetime.Shot, Expectation.Deleted, "texture-manager");
            session.Emit("texture-manager-ready", cell, shot, new
            {
                textureManagerId = id, role, initializedNow, cellLifetime,
                manager.WhiteTexture, whiteTextureLive = GL.IsTexture(manager.WhiteTexture)
            });
        }

        public void RendererReady(GlRenderer renderer, bool initializedNow)
        {
            string id = session.ObjectId(renderer, "renderer");
            shot.Renderers.Add(new(id, initializedNow));
            session.Emit("renderer-ready", cell, shot, new { rendererId = id, initializedNow });
        }

        public void ResidentOwnerReady(object resident, bool createdNow)
        {
            string id = session.ObjectId(resident, "resident");
            shot.ResidentOwners.Add(new(id, createdNow));
            session.Emit("resident-owner-ready", cell, shot, new { residentOwnerId = id, createdNow });
        }

        public void ResidentEpochAdvanced(long epoch)
        {
            shot.ResidentEpochs.Add(epoch);
            session.Emit("resident-epoch-advanced", cell, shot, new { epoch });
        }

        public void ManifestValidated(string phase, bool valid, int expectedCount, int foundCount)
        {
            shot.ManifestValidations.Add(new(phase, valid, expectedCount, foundCount));
            session.Emit("resident-manifest-validated", cell, shot, new { phase, valid, expectedCount, foundCount });
        }

        public void TextureAssigned(string category, string gamePath, string? resolvedPath, int handle, int white)
        {
            bool isStatic = category.StartsWith("static-", StringComparison.Ordinal);
            Track("texture", category, handle, isStatic ? Lifetime.Cell : Lifetime.Shot,
                Expectation.Deleted, "texture-manager");
            if (!isStatic && !string.IsNullOrEmpty(resolvedPath)) cell.ShotLocalDiskPaths.Add(resolvedPath);
            session.Emit("texture-assigned", cell, shot, new
            {
                category, gamePath, resolvedPath, handle, fallbackWhite = handle == white,
                live = handle > 0 && GL.IsTexture(handle)
            });
        }

        public void TrackOwnedTexture(string category, int handle)
            => Track("texture", category, handle, Lifetime.Shot, Expectation.Deleted, "shot");

        public void TrackCellOwnedTexture(string category, int handle)
            => Track("texture", category, handle, Lifetime.Cell, Expectation.Deleted, "built-scene");

        public void TrackTargets(int framebuffer, int colorTexture, int depthRenderbuffer,
            int msFramebuffer, int msColorRenderbuffer, int msDepthRenderbuffer)
        {
            Track("framebuffer", "target", framebuffer, Lifetime.Shot, Expectation.Deleted, "shot");
            Track("texture", "target-color", colorTexture, Lifetime.Shot, Expectation.Deleted, "shot");
            Track("renderbuffer", "target-depth", depthRenderbuffer, Lifetime.Shot, Expectation.Deleted, "shot");
            Track("framebuffer", "target-msaa", msFramebuffer, Lifetime.Shot, Expectation.Deleted, "shot");
            Track("renderbuffer", "target-msaa-color", msColorRenderbuffer, Lifetime.Shot, Expectation.Deleted, "shot");
            Track("renderbuffer", "target-msaa-depth", msDepthRenderbuffer, Lifetime.Shot, Expectation.Deleted, "shot");
            session.Emit("targets-created", cell, shot,
                new { framebuffer, colorTexture, depthRenderbuffer, msFramebuffer, msColorRenderbuffer, msDepthRenderbuffer });
        }

        public void TrackOverlayObjects(int program, int vao, int vbo)
        {
            Track("program", "overlay", program, Lifetime.Shot, Expectation.Deleted, "shot");
            Track("vertex-array", "overlay", vao, Lifetime.Shot, Expectation.Deleted, "shot");
            Track("buffer", "overlay", vbo, Lifetime.Shot, Expectation.Deleted, "shot");
            session.Emit("overlay-objects-created", cell, shot, new { program, vao, vbo });
        }

        /// <summary>The overlay's binding state at the moment it is deleted (Gate B2 review, C1).</summary>
        public void OverlayDeleteState(int program, int currentProgram, int currentVertexArray)
        {
            var row = new OverlayDelete(program, currentProgram, currentVertexArray, currentProgram == program);
            shot.OverlayDeletes.Add(row);
            session.Emit("overlay-delete-state", cell, shot, row);
        }

        /// <summary>Call only after a real GlMesh.Upload succeeds. Returns its exact input hash so
        /// a later per-shot binding can be compared without calling Upload again.</summary>
        public string MeshUploaded(string category, GlMesh mesh, float[] vertices, int[] indices)
        {
            string hash = HashArrays(vertices, indices);
            string meshId = session.ObjectId(mesh, "mesh");
            var lifetimeKind = category.StartsWith("static-", StringComparison.Ordinal) ? Lifetime.Cell : Lifetime.Shot;
            Track("vertex-array", category, mesh.Vao, lifetimeKind, Expectation.Deleted, "mesh");
            Track("buffer", category + ":vbo", mesh.Vbo, lifetimeKind, Expectation.Deleted, "mesh");
            Track("buffer", category + ":ebo", mesh.Ebo, lifetimeKind, Expectation.Deleted, "mesh");
            var upload = new Upload(category, meshId, hash, vertices.Length * sizeof(float), indices.Length * sizeof(int),
                mesh.Vao, mesh.Vbo, mesh.Ebo,
                mesh.Vao > 0 && GL.IsVertexArray(mesh.Vao) && GL.IsBuffer(mesh.Vbo) && GL.IsBuffer(mesh.Ebo));
            shot.Uploads.Add(upload);
            if (lifetimeKind == Lifetime.Cell && shot.FirstStaticUploadOrder == 0) shot.FirstStaticUploadOrder = session.OrderMark();
            session.Emit("mesh-uploaded", cell, shot, upload);
            return hash;
        }

        /// <summary>Per-shot draw binding, distinct from Upload. A retained GREEN scene emits this
        /// for every static mesh on both shots while MeshUploaded occurs only during install.</summary>
        public void StaticBound(string category, GlMesh mesh, string inputSha256)
        {
            var binding = new StaticBinding(category, session.ObjectId(mesh, "mesh"), inputSha256,
                shot.ContextId, mesh.Vao, mesh.Vbo, mesh.Ebo,
                mesh.Vao > 0 && GL.IsVertexArray(mesh.Vao) && GL.IsBuffer(mesh.Vbo) && GL.IsBuffer(mesh.Ebo));
            shot.StaticBindings.Add(binding);
            cell.StaticMeshIds.Add(binding.MeshId);   // G3
            session.Emit("static-bound-for-shot", cell, shot, binding);
        }

        public void MeshAdded(string category, GlMesh mesh, bool borrowed = false)
        {
            if (borrowed)
            {
                cell.BorrowedMeshIds.Add(session.ObjectId(mesh, "mesh"));   // G3
                cell.BorrowedMeshes.Add((mesh, mesh.Vao, mesh.Vbo, mesh.Ebo, session.ObjectId(mesh, "mesh")));   // G3 Q4
                Track("vertex-array", category, mesh.Vao, Lifetime.Shot, Expectation.Alive, "borrowed-npc");
                Track("buffer", category + ":vbo", mesh.Vbo, Lifetime.Shot, Expectation.Alive, "borrowed-npc");
                Track("buffer", category + ":ebo", mesh.Ebo, Lifetime.Shot, Expectation.Alive, "borrowed-npc");
            }
            session.Emit("mesh-added", cell, shot,
                new { category, meshId = session.ObjectId(mesh, "mesh"), borrowed, mesh.Vao, mesh.Vbo, mesh.Ebo });
        }

        public void MeshRemoved(string category, GlMesh mesh, bool borrowed = false)
        {
            session.Emit("mesh-removed", cell, shot, new
            {
                category, meshId = session.ObjectId(mesh, "mesh"), borrowed,
                vaoStillLive = mesh.Vao > 0 && GL.IsVertexArray(mesh.Vao),
                vboStillLive = mesh.Vbo > 0 && GL.IsBuffer(mesh.Vbo),
                eboStillLive = mesh.Ebo > 0 && GL.IsBuffer(mesh.Ebo)
            });
        }

        public void RawBufferUploaded(string family, int handle, float[] data, string? layer = null)
        {
            var upload = new RawUpload(family, HashFloats(data), data.Length * sizeof(float), handle, layer);
            shot.RawUploads.Add(upload);
            session.Emit("raw-buffer-uploaded", cell, shot, upload);
        }

        // ---- G3 RED (launch 8): measurement only; every call site is null-conditional. ----

        /// <summary>What the renderer holds: this cell's static-bound meshes, borrowed NPC meshes, anything else.</summary>
        public void RendererMembership(string phase, GlRenderer renderer)
        {
            var ids = renderer.Meshes.Select(m => session.ObjectId(m, "mesh")).ToArray();
            int statics = ids.Count(cell.StaticMeshIds.Contains), borrowed = ids.Count(cell.BorrowedMeshIds.Contains);
            session.Emit("renderer-membership", cell, shot, new
            {
                phase, rendererId = session.ObjectId(renderer, "renderer"), total = ids.Length, staticBound = statics,
                borrowedNpc = borrowed, other = ids.Length - statics - borrowed,
                otherIds = ids.Where(x => !cell.StaticMeshIds.Contains(x) && !cell.BorrowedMeshIds.Contains(x)).Take(64).ToArray(),
                borrowedIds = ids.Where(cell.BorrowedMeshIds.Contains).ToArray()
            });
        }

        public void SceneDepth(string? sceneDepthSha256)
            => session.Emit("scene-depth", cell, shot, new { sceneDepthSha256 });

        /// <summary>One per-shape isolated depth draw: renderer meshes rendering inside and outside the isolated set.</summary>
        public void IsolationDraw(int drawIndex, IReadOnlySet<GlMesh> isolated, GlRenderer renderer, string? isolatedDepthSha256,
            int rasterPixels, int visiblePixels, int hiddenPixels)
        {
            int inside = 0, outside = 0, outsideStatic = 0;
            foreach (var m in renderer.Meshes)
            {
                if (!m.IsRendering) continue;
                if (isolated.Contains(m)) inside++;
                else { outside++; if (cell.StaticMeshIds.Contains(session.ObjectId(m, "mesh"))) outsideStatic++; }
            }
            session.Emit("isolation-draw", cell, shot, new
            {
                drawIndex, isolatedCount = isolated.Count, rendererMeshes = renderer.Meshes.Count, renderingInside = inside,
                renderingOutside = outside, renderingOutsideStatic = outsideStatic, isolatedDepthSha256,
                rasterPixels, visiblePixels, hiddenPixels
            });
        }

        public void Checkpoint(string boundary)
        {
            shot.Checkpoints.Add(boundary);
            session.Emit("checkpoint", cell, shot, new { boundary });
            if (StringComparer.Ordinal.Equals(session.RequestedFault, boundary))
            {
                session.RequestedFaultReached = true;
                throw new ExpectedGraphicsFault(boundary, cell.CellId, shot.ShotId);
            }
        }

        void Track(string kind, string category, int handle, Lifetime ownerLifetime,
            Expectation afterLifetime, string owner)
        {
            if (handle <= 0 || lifetime is null) return;
            var resource = new TrackedResource(shot.ContextId, kind, category, handle, ownerLifetime, afterLifetime, owner);
            lifetime.Track(resource);
            if (ownerLifetime == Lifetime.Cell) cell.TrackCellResource(resource);
        }

        /// <summary>G2 (Gate B4): per-component digests of the static key's inputs, computed independently of
        /// StaticInputKey (which is not changed), so each arm shows which input moved the key. The first time a
        /// reference set is seen, its references are listed for fixture selection.</summary>
        public void StaticKeyInputs(string key, string preparedIdentity, IReadOnlyList<SceneRef> refs,
            IReadOnlyDictionary<string, List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C)>> terrainPatches,
            IReadOnlyList<(string Diffuse, List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C, float Oa, float Ob, float Oc)> Tris)> terrainLayers,
            IReadOnlyDictionary<Mutagen.Bethesda.Plugins.FormKey, Covering> coverings, bool keepFx, bool fxStatic,
            float? cullAbove, bool refSurface, float refSurfaceOpacity)
        {
            static string D(IEnumerable<string> parts)
            {
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var p in parts) { h.AppendData(System.Text.Encoding.UTF8.GetBytes(p)); h.AppendData(new byte[] { 0 }); }
                return Convert.ToHexString(h.GetHashAndReset());
            }
            static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            static bool IsFx(SceneRef r) => r.MeshRelPath.Replace('/', '\\').Contains("effects\\", StringComparison.OrdinalIgnoreCase);
            var inputs = new KeyInputs(
                key, D([preparedIdentity]),
                $"fx={keepFx};fxStatic={fxStatic};cull={cullAbove?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};surface={refSurface};opacity={F(refSurfaceOpacity)}",
                D([Environment.GetEnvironmentVariable("VP_LAYER_ONLY") ?? "<null>", Environment.GetEnvironmentVariable("VP_LAYER_SOLID") ?? "<null>",
                   Environment.GetEnvironmentVariable("VP_FX_DOUBLESIDED") ?? "<null>"]),
                refs.Count, D(refs.Select(r => r.ToString())),
                D(refs.Select(r => $"{r.MeshRelPath}|{r.RefKey}|{r.BaseKey}|{r.Position}|{r.Rotation}|{F(r.Scale)}")),
                refs.Count(r => r.Highlight),
                D(refs.Where(r => r.Highlight || r.HighlightColor != default).Select(r => $"{r.RefKey}|{r.Highlight}|{r.HighlightColor}")),
                refs.Count(r => r.Marker), refs.Count(r => r.Constructed), refs.Count(r => r.StateChange != 0),
                D(refs.Where(r => r.StateChange != 0 || r.StateLabel is not null).Select(r => $"{r.RefKey}|{r.StateChange}|{r.StateLabel}")),
                refs.Count(r => r.BosRecordBase is not null), refs.Count(IsFx),
                D(terrainPatches.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(p => new[] { "patch:" + p.Key }.Concat(p.Value.Select(t => $"{t.A}|{t.B}|{t.C}")))),
                D(terrainLayers.SelectMany(l => new[] { "layer:" + l.Diffuse }
                    .Concat(l.Tris.Select(t => $"{t.A}|{t.B}|{t.C}|{F(t.Oa)}|{F(t.Ob)}|{F(t.Oc)}")))),
                D(coverings.OrderBy(c => c.Key).Select(c => $"{c.Key}|{c.Value}")));
            shot.KeyInputs.Add(inputs);
            session.Emit("static-key-inputs", cell, shot, inputs);
            if (session.SeenReferenceDigests.Add(inputs.RefsSha256))
                session.Emit("static-refs-sample", cell, shot, new
                {
                    inputs.RefsSha256,
                    refs = refs.Select(r => new
                    {
                        refKey = r.RefKey.ToString(), baseKey = r.BaseKey.ToString(), r.MeshRelPath,
                        position = r.Position.ToString(), r.Highlight, r.Marker, r.Constructed, r.StateChange, r.StateLabel
                    }).ToArray()
                });
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            shot.Ended = true;
            // G2 (Gate B4): the process-identity arm moves one identity file's write time after the first shot, so
            // the next shot's process key changes inside one process. Only VP_GRAPHICS_REUSE_RED_TOUCH enables it.
            if (session.TouchPath is { } touch && !session.Touched)
            {
                session.Touched = true;
                string before = PreparedProcessIdentity.FileToken(touch);
                File.SetLastWriteTimeUtc(touch, new FileInfo(touch).LastWriteTimeUtc.AddSeconds(2));
                string after = PreparedProcessIdentity.FileToken(touch);
                session.Emit("identity-touched", cell, shot, new { path = touch, before, after, changed = before != after });
            }
            session.Emit("shot-end", cell, shot, new { shot.ShotId });
        }
    }

    sealed class GraphicsLifetime : IDisposable
    {
        readonly Session session; readonly CellState cell; readonly ShotState shot;
        readonly Dictionary<string, TrackedResource> shotResources = new(StringComparer.Ordinal);
        bool disposed;
        internal GraphicsLifetime(Session session, CellState cell, ShotState shot)
        { this.session = session; this.cell = cell; this.shot = shot; }
        internal void Track(TrackedResource resource)
        {
            if (resource.OwnerLifetime == Lifetime.Shot)
                shotResources.TryAdd(resource.Key, resource);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            string? version = GL.GetString(StringName.Version);
            bool current = !string.IsNullOrWhiteSpace(version);
            var shotChecks = shotResources.Values.Select(r => ResourceCheck.For(r, current)).ToArray();
            var cellChecks = cell.CellResources.Where(r => r.ContextId == shot.ContextId)
                .Select(r => ResourceCheck.For(r with { AfterLifetime = Expectation.Alive }, current)).ToArray();
            shot.ShotTeardownChecks.AddRange(shotChecks);
            shot.CellSurvivalChecks.AddRange(cellChecks);
            shot.TeardownContextCurrent = current;
            session.Emit("shot-after-owner-dispose", cell, shot,
                new { contextCurrent = current, glVersion = version, shotChecks, cellChecks });
        }
    }

    internal enum Lifetime { Shot, Cell }
    internal enum Expectation { Deleted, Alive }
    internal sealed record TrackedResource(string ContextId, string Kind, string Category, int Handle,
        Lifetime OwnerLifetime, Expectation AfterLifetime, string Owner)
    { internal string Key => $"{ContextId}:{Kind}:{Handle}:{OwnerLifetime}"; }
    internal sealed record ResourceCheck(string ContextId, string Kind, string Category, int Handle,
        string Expected, string Owner, bool? Live, bool Pass, bool? ProgramDeleteStatus = null)
    {
        internal static ResourceCheck For(TrackedResource r, bool current)
        {
            bool? live = current ? IsLive(r.Kind, r.Handle) : null;
            bool pass = live is not null && (r.AfterLifetime == Expectation.Alive ? live.Value : !live.Value);
            // A program still live at check time records GL_DELETE_STATUS, so "deleted while bound, deferred"
            // is measured rather than inferred (Gate B2 review, C1).
            bool? deleteStatus = null;
            if (r.Kind == "program" && live == true)
            {
                GL.GetProgram(r.Handle, GetProgramParameterName.DeleteStatus, out int status);
                deleteStatus = status != 0;
            }
            return new(r.ContextId, r.Kind, r.Category, r.Handle,
                r.AfterLifetime.ToString().ToLowerInvariant(), r.Owner, live, pass, deleteStatus);
        }
        static bool IsLive(string kind, int handle) => kind switch
        {
            "texture" => GL.IsTexture(handle), "buffer" => GL.IsBuffer(handle),
            "vertex-array" => GL.IsVertexArray(handle), "framebuffer" => GL.IsFramebuffer(handle),
            "renderbuffer" => GL.IsRenderbuffer(handle), "program" => GL.IsProgram(handle),
            _ => throw new InvalidOperationException("unknown GL resource kind: " + kind)
        };
    }

    internal sealed class Session
    {
        readonly object gate = new();
        readonly ConditionalWeakTable<object, Identity> identities = new();
        readonly List<CellState> cells = [];
        readonly string eventPath, dependencyAssembly, dependencySha256;
        int nextId; long sequence; long order; bool finished;
        internal string OutputPath { get; }
        internal string? RequestedFault { get; }
        internal bool RequestedFaultReached { get; set; }
        internal bool ExpectedFaultCaught { get; set; }
        internal bool UnexpectedFaultCaught { get; set; }
        // G2 (Gate B4): VP_GRAPHICS_REUSE_RED_G2=1 makes Finish report instrumentation completeness only (the G2
        // driver assesses the receipt); the residency and touch variables are accepted only in that mode.
        internal bool G2Mode { get; }
        internal string? ResidencyMode { get; }
        internal bool ResidencyFired { get; set; }
        internal string? TouchPath { get; }
        internal bool Touched { get; set; }
        internal HashSet<string> SeenReferenceDigests { get; } = new(StringComparer.Ordinal);
        // G4/G5 (launch 9, Gate B8 RED): measurement variables, accepted only in G2 mode.
        internal string? G4Mode { get; }
        internal long? G4BudgetBytes { get; }
        internal long G4RepollDeficitBytes { get; }
        internal string G4OomCategory { get; } = "static-terrain-diffuse";
        // B10b (part 1b RED): the ceiling arm's requested OOM ceiling, applied just before a repolling pass.
        internal long? G4CeilingBytes { get; }
        internal bool G5Observe { get; }
        internal List<string> G5Poison { get; } = [];
        internal int G5Shot { get; } = 2;
        internal string? G5NoOp { get; }
        internal static readonly string[] G5Items =
        [
            "mesh-visibility", "mesh-emissive-tint", "mesh-emissive-flags", "mesh-material",
            "renderer-core", "renderer-lights", "renderer-extra",
            "gl-framebuffer", "gl-viewport", "gl-clearcolor", "gl-depth-test", "gl-depth-func", "gl-depth-write", "gl-cull",
            "gl-frontface", "gl-blend", "gl-blendfunc", "gl-multisample", "gl-a2c", "gl-clip", "gl-program", "gl-vao-attribs",
            "gl-scissor", "gl-colormask", "gl-stencil", "gl-depthrange", "gl-blendeq", "gl-polyoffset", "gl-srgb",
            "gl-activetexture",
        ];

        internal Session(string outputPath, string? requestedFault)
        {
            OutputPath = outputPath;
            RequestedFault = string.IsNullOrWhiteSpace(requestedFault) ? null : requestedFault;
            if (RequestedFault is not null && RequestedFault is not ("after-static" or "after-dynamic" or "before-encode" or "after-shots"))
                throw new ArgumentException("VP_GRAPHICS_REUSE_RED_FAULT must be after-static, after-dynamic, before-encode or after-shots");
            G2Mode = Environment.GetEnvironmentVariable("VP_GRAPHICS_REUSE_RED_G2") == "1";
            string? residency = Environment.GetEnvironmentVariable("VP_GRAPHICS_REUSE_RED_RESIDENCY");
            ResidencyMode = string.IsNullOrWhiteSpace(residency) ? null : residency;
            string? touch = Environment.GetEnvironmentVariable("VP_GRAPHICS_REUSE_RED_TOUCH");
            TouchPath = string.IsNullOrWhiteSpace(touch) ? null : Path.GetFullPath(touch);
            if ((ResidencyMode is not null || TouchPath is not null) && !G2Mode)
                throw new ArgumentException("VP_GRAPHICS_REUSE_RED_RESIDENCY and _TOUCH require VP_GRAPHICS_REUSE_RED_G2=1");
            if (ResidencyMode is not null && ResidencyMode is not ("delete" or "replace" or "evict" or "replace-after-before-static"))
                throw new ArgumentException("VP_GRAPHICS_REUSE_RED_RESIDENCY must be delete, replace, evict or replace-after-before-static");
            if (TouchPath is not null && !File.Exists(TouchPath))
                throw new ArgumentException("VP_GRAPHICS_REUSE_RED_TOUCH names no file: " + TouchPath);
            // G4/G5 (launch 9, Gate B8 RED): measurement variables, accepted only in G2 mode.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string? g4 = Environment.GetEnvironmentVariable("VP_GRAPHICS_REUSE_RED_G4");
            G4Mode = string.IsNullOrWhiteSpace(g4) ? null : g4;
            string? budget = Environment.GetEnvironmentVariable("VP_GRAPHICS_REUSE_RED_BUDGET");
            G4BudgetBytes = string.IsNullOrWhiteSpace(budget) ? null : long.Parse(budget, inv);
            string? deficit = Environment.GetEnvironmentVariable("VP_GRAPHICS_REUSE_RED_REPOLL_DEFICIT_MIB");
            G4RepollDeficitBytes = (string.IsNullOrWhiteSpace(deficit) ? 256L : long.Parse(deficit, inv)) << 20;
            string? oomCategory = Environment.GetEnvironmentVariable("VP_GRAPHICS_REUSE_RED_OOM_CATEGORY");
            if (!string.IsNullOrWhiteSpace(oomCategory)) G4OomCategory = oomCategory;
            string? ceiling = Environment.GetEnvironmentVariable("VP_GRAPHICS_REUSE_RED_CEILING_MIB");
            G4CeilingBytes = string.IsNullOrWhiteSpace(ceiling) ? null : long.Parse(ceiling, inv) << 20;
            string? g5 = Environment.GetEnvironmentVariable("VP_GRAPHICS_REUSE_RED_G5");
            G5Observe = !string.IsNullOrWhiteSpace(g5);
            if (G5Observe && g5 != "observe")
                G5Poison = g5 == "all" ? [.. G5Items]
                    : [.. g5!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
            string? g5Shot = Environment.GetEnvironmentVariable("VP_GRAPHICS_REUSE_RED_G5_SHOT");
            if (!string.IsNullOrWhiteSpace(g5Shot)) G5Shot = int.Parse(g5Shot, inv);
            string? g5NoOp = Environment.GetEnvironmentVariable("VP_GRAPHICS_REUSE_RED_G5_NOOP");
            G5NoOp = string.IsNullOrWhiteSpace(g5NoOp) ? null : g5NoOp;
            if ((G4Mode is not null || G4BudgetBytes is not null || G5Observe) && !G2Mode)
                throw new ArgumentException("VP_GRAPHICS_REUSE_RED_G4, _BUDGET and _G5 require VP_GRAPHICS_REUSE_RED_G2=1");
            if (G4Mode is not null && G4Mode is not ("pin" or "repoll" or "oom" or "passes" or "ceiling"))
                throw new ArgumentException("VP_GRAPHICS_REUSE_RED_G4 must be pin, repoll, oom, passes or ceiling");
            if ((G4Mode == "ceiling") != (G4CeilingBytes is not null))
                throw new ArgumentException("VP_GRAPHICS_REUSE_RED_CEILING_MIB belongs to, and is required by, the ceiling arm");
            if (G4BudgetBytes is not null && G4Mode != "pin")
                throw new ArgumentException("VP_GRAPHICS_REUSE_RED_BUDGET belongs to the pin arm (a pinned budget disables the repoll)");
            foreach (string item in G5Poison)
                if (!G5Items.Contains(item)) throw new ArgumentException("unknown VP_GRAPHICS_REUSE_RED_G5 item " + item);
            if (G5NoOp is not null && !G5Poison.Contains(G5NoOp))
                throw new ArgumentException("VP_GRAPHICS_REUSE_RED_G5_NOOP must name one of the poisoned items");
            if (G2Mode) ValidateReflection();
            eventPath = outputPath + ".events.jsonl";
            dependencyAssembly = typeof(GlRenderer).Assembly.Location;
            dependencySha256 = HashFile(dependencyAssembly);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            if (File.Exists(outputPath) || File.Exists(eventPath))
                throw new IOException("graphics RED requires fresh evidence paths: " + outputPath);
            Emit("session-begin", null, null, new { outputPath, eventPath, RequestedFault,
                processId = Environment.ProcessId, startedUtc = DateTimeOffset.UtcNow });
        }

        internal string NextId(string prefix) { lock (gate) return $"{prefix}-{++nextId:D6}"; }
        internal long OrderMark() { lock (gate) return ++order; }
        internal string ObjectId(object value, string prefix)
        {
            lock (gate) return identities.GetValue(value, _ => new(NextId(prefix))).Value;
        }
        internal CellState BeginCell(string label, int index)
        {
            lock (gate)
            {
                var state = new CellState(NextId("cell"), label, index);
                cells.Add(state); Emit("cell-begin", state, null, new { state.CellId, label, index });
                return state;
            }
        }
        internal void Emit(string kind, CellState? cell, ShotState? shot, object payload)
        {
            lock (gate)
            {
                var line = new { schema = "viewprobe-graphics-reuse-red-event-v1", sequence = ++sequence,
                    utc = DateTimeOffset.UtcNow, kind, cellId = cell?.CellId, shotId = shot?.ShotId, payload };
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(line);
                using (var stream = new FileStream(eventPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                { stream.Write(bytes); stream.WriteByte((byte)'\n'); stream.Flush(true); }
                // Per-operation evidence stays append-only, while the full cumulative receipt is
                // bounded to lifecycle checkpoints. Re-evaluating and serializing every growing
                // mesh/resource list made an exterior RED quadratic in observer work.
                if (kind is "session-begin" or "shot-after-owner-dispose" or "cell-end"
                    or "expected-fault-caught" or "unexpected-fault-caught" or "session-finish")
                    WriteReceipt(kind);
            }
        }
        internal bool Finish()
        {
            lock (gate)
            {
                finished = true; Emit("session-finish", null, null, new { });
                var result = Evaluate();
                if (G2Mode) return !UnexpectedFaultCaught;
                return RequestedFault is null ? result.InstrumentationValid && result.RetentionAcceptance
                                              : result.FaultEvidenceComplete;
            }
        }

        void WriteReceipt(string lastEvent)
        {
            var receipt = new
            {
                schema = "viewprobe-graphics-reuse-red-v1", status = finished ? "finished" : "in-progress",
                lastEvent, lastSequence = sequence, eventLog = eventPath, RequestedFault,
                dependency = new { assembly = dependencyAssembly, sha256 = dependencySha256 },
                cells = cells.Select(CellBlock).ToArray(), evaluation = Evaluate()
            };
            string temp = OutputPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(receipt, JsonOptions));
            File.Move(temp, OutputPath, true);
        }

        Evaluation Evaluate()
        {
            var first = cells.FirstOrDefault(); var a = first?.Shots.ElementAtOrDefault(0);
            var b = first?.Shots.ElementAtOrDefault(1); var next = cells.Skip(1).SelectMany(c => c.Shots).FirstOrDefault();
            bool two = a is not null && b is not null, nextSeen = next is not null;
            string[] BindingInputs(ShotState? s) => s?.StaticBindings.Select(x => x.Category + ":" + x.InputSha256).ToArray() ?? [];
            string[] BindingHandles(ShotState? s) => s?.StaticBindings.Select(x =>
                $"{x.Category}:{x.ContextId}:{x.Vao}:{x.Vbo}:{x.Ebo}").ToArray() ?? [];
            bool bindingsPresent = two && a!.StaticBindings.Count > 0 && b!.StaticBindings.Count > 0;
            bool equalInputs = bindingsPresent && BindingInputs(a).SequenceEqual(BindingInputs(b), StringComparer.Ordinal);
            bool sameBindings = equalInputs && BindingHandles(a).SequenceEqual(BindingHandles(b), StringComparer.Ordinal);
            bool oneContext = two && a!.ContextId == b!.ContextId;
            string? InstallManager(ShotState s) => s.TextureManagers.FirstOrDefault(x => x.Role == "install")?.Id;
            string? Renderer(ShotState s) => s.Renderers.FirstOrDefault()?.Id;
            string? Resident(ShotState s) => s.ResidentOwners.FirstOrDefault()?.Id;
            bool sameOwners = two && Renderer(a!) is { } ar && ar == Renderer(b!)
                && InstallManager(a!) is { } am && am == InstallManager(b!)
                && Resident(a!) is { } ao && ao == Resident(b!);
            int rendererInits = first?.Shots.SelectMany(s => s.Renderers).Count(x => x.InitializedNow) ?? 0;
            int managerInits = first?.Shots.SelectMany(s => s.TextureManagers)
                .Count(x => x.Role == "install" && x.InitializedNow) ?? 0;
            int residentCreates = first?.Shots.SelectMany(s => s.ResidentOwners).Count(x => x.CreatedNow) ?? 0;
            var firstStaticUploads = a?.Uploads.Where(x => x.Category.StartsWith("static-", StringComparison.Ordinal)).ToArray() ?? [];
            var secondStaticUploads = b?.Uploads.Where(x => x.Category.StartsWith("static-", StringComparison.Ordinal)).ToArray() ?? [];
            bool oneInitialUploadPerBindingIdentity = bindingsPresent
                && firstStaticUploads.Length == a!.StaticBindings.Count
                && a.StaticBindings.All(binding => firstStaticUploads.Count(upload =>
                    upload.MeshId == binding.MeshId && upload.Category == binding.Category
                    && upload.Sha256 == binding.InputSha256) == 1)
                && secondStaticUploads.Length == 0;
            bool epochs = two && a!.ResidentEpochs.Count == 1 && b!.ResidentEpochs.Count == 1;
            bool ValidManifests(ShotState s) => s.ManifestValidations.Count == 2
                && s.ManifestValidations[0].Phase == "before-static"
                && s.ManifestValidations[1].Phase == "after-static"
                && s.ManifestValidations.All(x => x.Valid)
                && s.ManifestValidations[1].ExpectedCount == s.ManifestValidations[1].FoundCount;
            bool manifests = two && ValidManifests(a!) && ValidManifests(b!);
            // G2 F2: a replacement's retired set must be proven dead by its dispose record, after the decision to
            // replace and before the replacement's first static upload (the survival set no longer carries it).
            bool retirementVerified = cells.All(c => c.Replacements.All(r =>
            {
                var d = c.SceneDisposals.FirstOrDefault(x => x.SceneId == r.OldSceneId);
                long first = c.Shots.FirstOrDefault(s => s.ShotId == r.ShotId)?.FirstStaticUploadOrder ?? 0;
                return d is not null && d.AllDeadAfter && d.Order > r.Order && (first == 0 || d.Order < first);
            }));
            bool shotCleanup = retirementVerified && cells.SelectMany(c => c.Shots).All(s => s.TeardownContextCurrent == true
                && s.ShotTeardownChecks.All(x => x.Pass) && s.CellSurvivalChecks.All(x => x.Pass));
            bool cellCleanup = cells.All(c => c.CellTeardownContextCurrent == true && c.CellTeardownChecks.All(x => x.Pass));
            bool liveBoundaries = cells.SelectMany(c => c.Shots).SelectMany(s => s.Uploads).All(x => x.LiveAtBoundary)
                && cells.SelectMany(c => c.Shots).SelectMany(s => s.StaticBindings).All(x => x.LiveAtBoundary);
            string Family(Func<GraphicsShotFacts, int> population, Func<ShotState, IEnumerable<string>> hashes)
            {
                if (!two || population(a!.Facts) == 0 || population(b!.Facts) == 0) return "unassessed";
                var x = hashes(a).ToArray(); var y = hashes(b).ToArray();
                if (x.Length == 0 || y.Length == 0) return "instrumentation-missing";
                return x.SequenceEqual(y, StringComparer.Ordinal) ? "unchanged" : "changed";
            }
            string water = Family(f => f.WaterPopulation,
                s => s.Uploads.Where(x => x.Category == "dynamic-water").Select(x => x.Sha256));
            string overlays = Family(f => f.NavTrianglePopulation + f.RedTrianglePopulation + f.RoutePopulation
                    + f.GridPopulation + f.OverlayLinePopulation + f.PlanePopulation,
                s => s.RawUploads.Where(x => !x.Family.StartsWith("subject-mask", StringComparison.Ordinal))
                    .Select(x => x.Family + ":" + x.Sha256));
            string masks = Family(f => f.SubjectMaskPopulation,
                s => s.RawUploads.Where(x => x.Family.StartsWith("subject-mask", StringComparison.Ordinal))
                    .Select(x => x.Family + ":" + x.Sha256));
            string npcs = !two || a!.Facts.NpcPopulation == 0 || b!.Facts.NpcPopulation == 0 ? "unassessed"
                : a.ShotTeardownChecks.Any(x => x.Owner == "borrowed-npc") && b.ShotTeardownChecks.Any(x => x.Owner == "borrowed-npc")
                    ? "borrowed-lifetime-assessed" : "instrumentation-missing";
            bool dynamicExercise = water == "changed" || overlays == "changed" || masks == "changed";
            bool fixture = two && nextSeen && bindingsPresent && equalInputs && dynamicExercise;
            bool nextOwners = nextSeen && next!.ContextId != b?.ContextId && Renderer(next) != Renderer(b!)
                && InstallManager(next) != InstallManager(b!) && Resident(next) != Resident(b!);
            bool retention = fixture && oneContext && sameOwners && rendererInits == 1 && managerInits == 1
                && residentCreates == 1 && epochs && manifests && sameBindings && oneInitialUploadPerBindingIdentity
                && nextOwners && shotCleanup && cellCleanup && liveBoundaries;
            bool faultChecks = cells.SelectMany(c => c.Shots).Any(s => s.TeardownContextCurrent == true
                && s.ShotTeardownChecks.Count + s.CellSurvivalChecks.Count > 0);
            bool faultOrdering = cells.Any(c => c.ContextOwnerEndCount > 0 && c.Disposed
                && c.LastContextOwnerEndOrder > 0 && c.CellFinallyOrder > c.LastContextOwnerEndOrder);
            bool faultEvidence = RequestedFault is not null && RequestedFaultReached && ExpectedFaultCaught
                && !UnexpectedFaultCaught && faultChecks && faultOrdering;
            bool instrumentation = RequestedFault is not null ? faultEvidence
                : fixture && liveBoundaries && cells.SelectMany(c => c.Shots).All(s => s.TeardownContextCurrent == true);
            return new(instrumentation, retention, faultEvidence, fixture, two, nextSeen, oneContext,
                sameOwners, rendererInits, managerInits, residentCreates,
                cells.SelectMany(c => c.Shots).Sum(s => s.ResidentEpochs.Count),
                cells.SelectMany(c => c.Shots).Sum(s => s.ManifestValidations.Count),
                bindingsPresent, equalInputs, sameBindings, oneInitialUploadPerBindingIdentity, dynamicExercise,
                nextOwners, shotCleanup, cellCleanup, liveBoundaries, water, overlays, npcs, masks, retirementVerified);
        }

        static object CellBlock(CellState c) => new
        {
            c.CellId, c.Label, c.Index, c.Disposed, contextIds = c.ContextIds,
            c.ContextOwnerEndCount, c.LastContextOwnerEndOrder, c.CellFinallyOrder,
            cellTeardownContextCurrent = c.CellTeardownContextCurrent,
            cellTeardownChecks = c.CellTeardownChecks, residentAtCellEnd = c.ResidentAtCellEnd,
            residentAtCellEndG2 = c.ResidentAtCellEndG2, replacements = c.Replacements, sceneDisposals = c.SceneDisposals,
            retiredResidentPathCount = c.RetiredResidentPaths.Count, retiredTrackedResources = c.RetiredResources.Count,
            cacheClears = c.CacheClears, reuploads = c.Reuploads,
            shots = c.Shots.Select(s => new { s.ShotId, s.ContextId, s.Facts,
                keyInputs = s.KeyInputs, keyDecisions = s.KeyDecisions, manifestChecks = s.ManifestChecks,
                residencyMutations = s.ResidencyMutations,
                textureManagers = s.TextureManagers, renderers = s.Renderers,
                residentOwners = s.ResidentOwners, residentEpochs = s.ResidentEpochs,
                manifestValidations = s.ManifestValidations, uploads = s.Uploads,
                staticBindings = s.StaticBindings, rawUploads = s.RawUploads,
                checkpoints = s.Checkpoints, s.TeardownContextCurrent,
                shotTeardownChecks = s.ShotTeardownChecks, cellSurvivalChecks = s.CellSurvivalChecks,
                overlayDeletes = s.OverlayDeletes, s.Ended }).ToArray()
        };
        static string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    }

    sealed class Identity(string value) { internal string Value { get; } = value; }
    internal sealed class CellState(string id, string label, int index)
    {
        internal string CellId { get; } = id; internal string Label { get; } = label; internal int Index { get; } = index;
        internal HashSet<string> ContextIds { get; } = [];
        internal List<ShotState> Shots { get; } = [];
        internal List<TrackedResource> CellResources { get; } = [];
        internal List<ResourceCheck> CellTeardownChecks { get; } = [];
        internal bool? CellTeardownContextCurrent { get; set; }
        internal int ContextOwnerEndCount { get; set; }
        internal long LastContextOwnerEndOrder { get; set; }
        internal long CellFinallyOrder { get; set; }
        internal bool Disposed { get; set; }
        internal HashSet<string> ShotLocalDiskPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal ResidentInvariant? ResidentAtCellEnd { get; set; }
        internal HashSet<string> RetiredResidentPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<SceneReplacement> Replacements { get; } = [];
        internal List<SceneDisposal> SceneDisposals { get; } = [];
        internal List<int> OrphanHandles { get; } = [];
        internal ResidentInvariantG2? ResidentAtCellEndG2 { get; set; }
        internal List<TrackedResource> RetiredResources { get; } = [];
        internal List<CacheClear> CacheClears { get; } = [];
        internal List<ResidencyMutation> Mutations { get; } = [];
        internal List<ReuploadCheck> Reuploads { get; } = [];
        internal HashSet<string> StaticMeshIds { get; } = new(StringComparer.Ordinal);   // G3
        internal HashSet<string> BorrowedMeshIds { get; } = new(StringComparer.Ordinal); // G3
        // G3 (B6 review Q4): the borrowed wrappers and the handles recorded when each was added; RED session only.
        internal List<(GlMesh Mesh, int Vao, int Vbo, int Ebo, string Id)> BorrowedMeshes { get; } = [];
        internal ShotState BeginShot(string shotId, string contextId, GraphicsShotFacts facts)
        { var s = new ShotState(shotId, contextId, facts); Shots.Add(s); return s; }
        internal void TrackCellResource(TrackedResource resource)
        { if (!CellResources.Any(x => x.Key == resource.Key)) CellResources.Add(resource); }
    }
    internal sealed class ShotState(string id, string contextId, GraphicsShotFacts facts)
    {
        internal string ShotId { get; } = id; internal string ContextId { get; } = contextId;
        internal GraphicsShotFacts Facts { get; } = facts;
        internal List<ManagerUse> TextureManagers { get; } = []; internal List<OwnerUse> Renderers { get; } = [];
        internal List<ResidentUse> ResidentOwners { get; } = []; internal List<long> ResidentEpochs { get; } = [];
        internal List<ManifestValidation> ManifestValidations { get; } = []; internal List<Upload> Uploads { get; } = [];
        internal List<StaticBinding> StaticBindings { get; } = []; internal List<RawUpload> RawUploads { get; } = [];
        internal List<string> Checkpoints { get; } = []; internal List<ResourceCheck> ShotTeardownChecks { get; } = [];
        internal List<ResourceCheck> CellSurvivalChecks { get; } = [];
        internal bool? TeardownContextCurrent { get; set; } internal bool Ended { get; set; }
        internal List<OverlayDelete> OverlayDeletes { get; } = [];
        internal List<KeyInputs> KeyInputs { get; } = [];
        internal List<KeyDecision> KeyDecisions { get; } = [];
        internal List<ManifestCheck> ManifestChecks { get; } = [];
        internal List<ResidencyMutation> ResidencyMutations { get; } = [];
        internal long FirstStaticUploadOrder { get; set; }
    }

    // ---- G2 (Gate B4) records ----
    internal sealed record KeyInputs(string Key, string PreparedIdentitySha256, string Flags, string EnvSha256,
        int RefCount, string RefsSha256, string GeometrySha256, int HighlightCount, string HighlightSha256,
        int MarkerCount, int ConstructedCount, int StateChangedCount, string StateSha256, int BosSwappedCount,
        int FxPathCount, string TerrainSha256, string LayersSha256, string CoveringsSha256);
    internal sealed record KeyDecision(string Key, string? PriorKey, string Decision, string SceneId, long Order);
    internal sealed record SceneReplacement(string OldSceneId, string OldKey, string NewKey, string? ShotId, long Order,
        int MeshCount, int OwnedTextureCount, int RetiredResidentPaths, bool AllLiveBeforeDispose, int RetiredTrackedResources);
    internal sealed record SceneDisposal(string SceneId, string Key, string? ShotId, long Order, int MeshCount,
        int OwnedTextureCount, int LiveMeshesAfter, int LiveOwnedAfter, bool AllDeadAfter);
    internal sealed record ResidencyMutation(string Mode, string Phase, string Category, string GamePath, string DiskPath,
        int Handle, int NewHandle, int ResidentPeekBefore, int ResidentPeekAfter, int PrivateCachePeekBefore,
        int PrivateCachePeekAfter, bool HandleLiveBefore, bool HandleLiveAfter, bool NewHandleLive, long Order, string? EntryId);
    internal sealed record ClearProbe(int White, bool WhiteLive, int MapCount, string[] MappedPaths, int MissingCount,
        int OwnedNonWhite, long Order);
    internal sealed record CacheClear(string? ShotId, int WhiteBefore, int WhiteAfter, bool WhiteLiveBefore, bool WhiteLiveAfter,
        int PrivateMapBefore, int PrivateMapAfter, int PathsPeeked, int PeekHitsAfter, int MissingBefore, int MissingAfter,
        int OwnedNonWhiteBefore, int OwnedNonWhiteAfter, long OrderBefore, long OrderAfter, bool Holds);
    internal sealed record ReuploadCheck(string? ShotId, string DiskPath, string GamePath, int EvictedHandle, int? ManifestHandle,
        int ResidentPeek, bool Live, string? EvictedEntryId, string? CurrentEntryId, int PrivateCachePeek, bool Holds);
    internal sealed record ManifestRow(string Category, string GamePath, string? DiskPath, int ManifestHandle,
        int ResidentPeek, int PrivateCachePeek, bool Live);
    internal sealed record ManifestCheck(string Phase, int Index, long Order, int Entries, int Mismatched, int Dead,
        int PrivateStale, bool AllOwnedAndLive, ManifestRow[] Rows);
    internal sealed record ResidentInvariantG2(int ResidentCount, int CurrentDistinctPaths, int CurrentFound,
        int RetiredOnlyPaths, int RetiredFound, string[] RetiredEvicted, int Surplus, string[] ShotLocalPathsInCache,
        bool Holds);

    // ---- G2 (Gate B4) non-promoting reads of the Rendering caches (public telemetry), plus the reflection the
    // fault-injection writes still need (entry identity, handle replace, evict) -- measurement only ----
    const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    static readonly System.Reflection.FieldInfo? ResidentMap = typeof(ResidentTextureCache).GetField("_map", Private);
    static readonly Type? ResidentEntry = typeof(ResidentTextureCache).GetNestedType("Entry", System.Reflection.BindingFlags.NonPublic);

    static void ValidateReflection()
    {
        string[] entryFields = ["Handle", "Bytes", "InProtected", "Node"];
        if (ResidentMap is null || ResidentEntry is null
            || entryFields.Any(f => ResidentEntry.GetField(f) is null)
            || typeof(ResidentTextureCache).GetField("_currentBytes", Private) is null
            || typeof(ResidentTextureCache).GetField("_protectedBytes", Private) is null)
            throw new InvalidOperationException("G2 observer: the Rendering cache fields it reads are not present in this build");
    }

    static int PeekResident(ResidentTextureCache cache, string? diskPath)
        => cache.TryPeek(diskPath, out int handle) ? handle : -1;

    string? ResidentEntryId(ResidentTextureCache cache, string? diskPath)
    {
        if (string.IsNullOrEmpty(diskPath)) return null;
        var map = (System.Collections.IDictionary)ResidentMap!.GetValue(cache)!;
        object? entry = map.Contains(diskPath) ? map[diskPath] : null;
        return entry is null ? null : session.ObjectId(entry, "resident-entry");
    }

    static int PeekPrivate(GlTextureManager manager, string gamePath)
        => manager.TryPeekCachedTexture(gamePath, out int handle) ? handle : -1;

    static void SetResidentHandle(ResidentTextureCache cache, string diskPath, int handle)
    {
        var map = (System.Collections.IDictionary)ResidentMap!.GetValue(cache)!;
        object entry = map[diskPath] ?? throw new InvalidOperationException("G2 replace: path not resident: " + diskPath);
        ResidentEntry!.GetField("Handle")!.SetValue(entry, handle);
    }

    /// <summary>Exactly what ResidentTextureCache.EvictToBudget does to one victim, minus the GL delete (the caller
    /// deletes): unlink from its segment, drop the map entry, subtract its bytes.</summary>
    static void EvictResident(ResidentTextureCache cache, string diskPath)
    {
        var map = (System.Collections.IDictionary)ResidentMap!.GetValue(cache)!;
        object entry = map[diskPath] ?? throw new InvalidOperationException("G2 evict: path not resident: " + diskPath);
        bool inProtected = (bool)ResidentEntry!.GetField("InProtected")!.GetValue(entry)!;
        long bytes = (long)ResidentEntry.GetField("Bytes")!.GetValue(entry)!;
        object node = ResidentEntry.GetField("Node")!.GetValue(entry)!;
        object list = node.GetType().GetProperty("List")!.GetValue(node)!;
        list.GetType().GetMethod("Remove", [node.GetType()])!.Invoke(list, [node]);
        map.Remove(diskPath);
        var current = typeof(ResidentTextureCache).GetField("_currentBytes", Private)!;
        current.SetValue(cache, (long)current.GetValue(cache)! - bytes);
        if (inProtected)
        {
            var prot = typeof(ResidentTextureCache).GetField("_protectedBytes", Private)!;
            prot.SetValue(cache, (long)prot.GetValue(cache)! - bytes);
        }
    }

    static int CreateSubstituteTexture()
    {
        int made = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, made);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1, 1, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 0, 255, 255, 255 });
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return made;
    }

    sealed class ExpectedGraphicsFault(string boundary, string cellId, string shotId)
        : Exception($"graphics reuse RED expected fault at {boundary} ({cellId}/{shotId})");
    internal sealed record OwnerUse(string Id, bool InitializedNow);
    internal sealed record ManagerUse(string Id, string Role, bool InitializedNow);
    internal sealed record ResidentUse(string Id, bool CreatedNow);
    internal sealed record ManifestValidation(string Phase, bool Valid, int ExpectedCount, int FoundCount);
    internal sealed record Upload(string Category, string MeshId, string Sha256, int VertexBytes, int IndexBytes,
        int Vao, int Vbo, int Ebo, bool LiveAtBoundary);
    internal sealed record StaticBinding(string Category, string MeshId, string InputSha256, string ContextId,
        int Vao, int Vbo, int Ebo, bool LiveAtBoundary);
    internal sealed record RawUpload(string Family, string Sha256, int Bytes, int Handle, string? Layer = null);
    internal sealed record OverlayDelete(int Program, int CurrentProgram, int CurrentVertexArray, bool BoundAtDelete);
    internal sealed record ResidentInvariant(int ResidentCount, int DistinctManifestResidentPaths, int ManifestPathsFoundInCache,
        int ShotLocalDiskPathsChecked, string[] ShotLocalPathsInCache, bool Holds);
    sealed record Evaluation(bool InstrumentationValid, bool RetentionAcceptance, bool FaultEvidenceComplete,
        bool FixtureReady, bool TwoSameCellShotsObserved, bool NextCellObserved, bool OneContextForSameCell,
        bool SameRendererManagerResidentOwners, int RendererInitializeCount, int InstallManagerInitializeCount,
        int ResidentOwnerCreateCount, int EpochCount, int ManifestValidationCount, bool StaticBindingsPresent,
        bool EqualStaticBindingInputs, bool SameStaticHandlesAcrossShots, bool OneInitialUploadPerStaticBindingIdentity,
        bool CameraDependentDynamicContentObserved, bool NewOwnersForNextCell, bool ShotCleanupPassed,
        bool CellCleanupPassed, bool LiveAtOperationBoundaries, string WaterStatus, string OverlayStatus,
        string NpcStatus, string SubjectMaskStatus, bool ReplacementRetirementVerified);

    static string HashArrays(float[] vertices, int[] indices)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(MemoryMarshal.AsBytes(vertices.AsSpan()));
        h.AppendData(MemoryMarshal.AsBytes(indices.AsSpan()));
        return Convert.ToHexString(h.GetHashAndReset());
    }
    static string HashFloats(float[] values) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));
    // ==== G4 / G5 (launch 9, Gate B8 RED): measurement-only hooks. Each returns at once unless its RED variable is
    // set, and those variables are accepted only with VP_GRAPHICS_REUSE_RED_G2=1 inside a RED session. ====

    /// <summary>G4 pin arm: the requested budget the cell hands its ResidentTextureCache (null without the variable).</summary>
    public long? TestBudgetBytes => session.G4BudgetBytes;

    readonly List<BallastEntry> g4Ballast = [];
    readonly List<int> g4Vram = [];
    long g4VramBytes;
    bool g4RepollFired, g4OomFired;
    int g45Fbo, g45FboTexture, g45Program, g45Vao;
    readonly HashSet<string> g45PreDrawShots = new(StringComparer.Ordinal);
    internal sealed record BallastEntry(string Key, int Handle, long EstimatedBytes, long AllocatedBytes, int ShotOrdinal, long Order);
    internal sealed record OomProbe(string Category, string GamePath, string DiskPath, string[] Drained, string ProbeError,
        long BudgetBefore, object? CeilingBefore, int WhiteBefore, long Order);
    static System.Globalization.CultureInfo Inv => System.Globalization.CultureInfo.InvariantCulture;


    static int FreeVramKb() => GL.GetInteger((GetPName)0x9049);   // GL_GPU_MEMORY_INFO_CURRENT_AVAILABLE_VIDMEM_NVX, as the dependency reads it

    static long PrivateBytes()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return p.PrivateMemorySize64;
    }

    static (string Category, string GamePath, string? DiskPath, int Handle, bool Resident)[] ResidentManifest(BuiltScene? scene) =>
        scene?.TextureManifest.Where(x => x.Resident && !string.IsNullOrEmpty(x.DiskPath)).ToArray()
        ?? Array.Empty<(string Category, string GamePath, string? DiskPath, int Handle, bool Resident)>();

    object ResidentView(ResidentTextureCache resident, BuiltScene? scene)
    {
        var manifest = ResidentManifest(scene);
        int member = 0, live = 0;
        foreach (var e in manifest)
        {
            if (PeekResident(resident, e.DiskPath) == e.Handle) member++;
            if (e.Handle > 0 && GL.IsTexture(e.Handle)) live++;
        }
        return new
        {
            budgetBytes = resident.BudgetBytes, currentBytes = resident.CurrentBytes, count = resident.Count,
            epoch = (object?)resident.Epoch, rendersSinceRepoll = (object?)resident.RendersSinceRepoll,
            overrideBudgetBytes = (object?)resident.OverrideBudgetBytes,
            vramQueryAvailable = (object?)resident.VramQueryAvailable,
            totalVramBytes = (object?)resident.TotalVramBytes, oomCeilingBytes = (object?)resident.OomCeilingBytes,
            retainedEntries = manifest.Length, retainedMember = member, retainedLive = live,
            freeVramKb = FreeVramKb(), privateBytes = PrivateBytes()
        };
    }

    static long LevelBytes(int handle)
    {
        long total = 0;
        for (int level = 0; level < 16; level++)
        {
            GL.GetTexLevelParameter(TextureTarget.Texture2D, level, GetTextureParameter.TextureWidth, out int w);
            GL.GetTexLevelParameter(TextureTarget.Texture2D, level, GetTextureParameter.TextureHeight, out int h);
            if (w <= 0 || h <= 0) break;
            total += (long)w * h * 4;
        }
        return total;
    }

    /// <summary>G4 pin arm: after this shot's first validation touched the retained manifest in the new epoch, allocate
    /// real 1024x1024 RGBA8 textures (full mip chain, the dependency's own upload shape) into the resident cache until its
    /// measured bytes exceed the effective budget (at least 16, at most 128 per shot), from the second shot on; then
    /// record the cache, every ballast entry's membership and liveness and the retained manifest.</summary>
    internal void PressureAfterTouch(ResidentTextureCache resident, BuiltScene scene)
    {
        ThrowIfDisposed();
        if (session.G4Mode != "pin") return;
        var shot = cell.Shots.LastOrDefault();
        int ordinal = cell.Shots.Count;
        var before = ResidentView(resident, scene);
        int made = 0;
        if (ordinal >= 2)
        {
            const int side = 1024;
            int prior = GL.GetInteger(GetPName.TextureBinding2D);
            var pixels = new byte[side * side * 4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 31);
            while (made < 128 && (made < 16 || resident.CurrentBytes <= resident.BudgetBytes))
            {
                int h = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, h);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, side, side, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
                long allocated = LevelBytes(h);
                long estimated = (long)side * side * 4 * 4 / 3;   // GlTextureManager.EstimateTextureBytes for this size
                string key = FormattableString.Invariant($"g4-ballast/{shot?.ShotId}/{made:D3}");
                resident.Add(key, h, estimated);
                g4Ballast.Add(new(key, h, estimated, allocated, ordinal, session.OrderMark()));
                made++;
            }
            GL.BindTexture(TextureTarget.Texture2D, prior);
        }
        var after = ResidentView(resident, scene);
        var rows = g4Ballast.Select(b => new
        {
            b.Key, b.Handle, b.ShotOrdinal, b.EstimatedBytes, b.AllocatedBytes,
            member = PeekResident(resident, b.Key) == b.Handle, live = b.Handle > 0 && GL.IsTexture(b.Handle)
        }).ToArray();
        session.Emit("g4-pressure", cell, shot, new
        {
            ordinal, made, before, after, ballastTotal = rows.Length, ballastMember = rows.Count(r => r.member),
            ballastLive = rows.Count(r => r.live), evictedKeys = rows.Where(r => !r.member).Select(r => r.Key).ToArray(),
            estimatedBytesThisShot = g4Ballast.Where(b => b.ShotOrdinal == ordinal).Sum(b => b.EstimatedBytes),
            allocatedBytesThisShot = g4Ballast.Where(b => b.ShotOrdinal == ordinal).Sum(b => b.AllocatedBytes),
            ballast = rows
        });
    }

    /// <summary>G4 repoll arm: immediately before the pass whose BeginRenderPass is the 16th since the last repoll, take
    /// real device memory (plain RGBA8 textures outside the cache, each cleared through a scratch framebuffer so the
    /// driver commits it) until the vendor free-memory reading is below the dependency's headroom by the requested
    /// deficit. Self-limited: stops on the target, on 3 GiB of process private-byte growth, or after 400 chunks.</summary>
    internal void BeforeRenderPass(ResidentTextureCache resident, BuiltScene? scene)
    {
        ThrowIfDisposed();
        if (session.G4Mode is "passes" or "ceiling") { PassRecord(resident, "before"); return; }   // B10b
        if (session.G4Mode != "repoll") return;
        var shot = cell.Shots.LastOrDefault();
        int ordinal = cell.Shots.Count;
        object? since = (object?)resident.RendersSinceRepoll;
        bool arm = !g4RepollFired && scene is not null && since is int n && n == 15;
        var view = ResidentView(resident, scene);
        if (!arm)
        {
            session.Emit("g4-pass", cell, shot, new { ordinal, phase = "before", view });
            return;
        }
        g4RepollFired = true;
        long total = resident.TotalVramBytes;
        long headroom = total > 0 ? Math.Max(1L << 30, (long)(total * 0.15)) : 1L << 30;   // ResidentTextureCache.ComputeDynamicBudget
        long deficit = session.G4RepollDeficitBytes;
        long targetFreeKb = (headroom - deficit) / 1024;
        long privateStart = PrivateBytes();
        var steps = new List<object>();
        string? stop = null;
        int prevDraw = GL.GetInteger(GetPName.DrawFramebufferBinding), prevRead = GL.GetInteger(GetPName.ReadFramebufferBinding);
        int prevTex = GL.GetInteger(GetPName.TextureBinding2D);
        var clear = new float[4];
        GL.GetFloat(GetPName.ColorClearValue, clear);
        int fbo = GL.GenFramebuffer();
        for (int i = 0; i < 400; i++)
        {
            int freeKb = FreeVramKb();
            if (freeKb <= targetFreeKb) { stop = "target-reached"; break; }
            if (PrivateBytes() - privateStart > (3L << 30)) { stop = "private-bytes-guard"; break; }
            long gapKb = freeKb - targetFreeKb;
            int w = 8192, h = gapKb > 512L * 1024 ? 4096 : 2048;   // 128 MiB chunks, 64 MiB near the target
            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            GL.ClearColor(0.25f, 0.5f, 0.75f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, 0, 0);
            GL.Finish();
            g4Vram.Add(tex);
            g4VramBytes += (long)w * h * 4;
            steps.Add(new { i, bytes = (long)w * h * 4, freeKbBefore = freeKb, freeKbAfter = FreeVramKb(), privateBytes = PrivateBytes() });
        }
        stop ??= "chunk-cap";
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, prevDraw);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, prevRead);
        GL.DeleteFramebuffer(fbo);
        GL.BindTexture(TextureTarget.Texture2D, prevTex);
        GL.ClearColor(clear[0], clear[1], clear[2], clear[3]);
        int freeNow = FreeVramKb();
        long current = resident.CurrentBytes;
        long ceiling = resident.OomCeilingBytes;
        long predicted = Math.Clamp(current + (long)freeNow * 1024 - headroom, 256L << 20, Math.Min(8L << 30, ceiling));
        session.Emit("g4-repoll-ballast", cell, shot, new
        {
            ordinal, view, totalVramBytes = total, headroomBytes = headroom, deficitBytes = deficit, targetFreeKb, stop,
            chunks = steps.Count, ballastBytes = g4VramBytes, freeKbAtRepoll = freeNow, privateBytesStart = privateStart,
            privateBytesNow = PrivateBytes(), predictedTargetBytes = predicted, budgetBytesNow = resident.BudgetBytes,
            currentBytesNow = current, predictedEviction = predicted < current && predicted != resident.BudgetBytes, steps
        });
    }

    /// <summary>G4 repoll arm: right after BeginRenderPass and before the first validation: the budget the repoll chose,
    /// which retained manifest entries it evicted (and whether their integers are still textures); then the device
    /// memory taken above is released.</summary>
    internal void AfterRenderPass(ResidentTextureCache resident, BuiltScene? scene)
    {
        ThrowIfDisposed();
        if (session.G4Mode is "passes" or "ceiling") { PassRecord(resident, "after"); return; }   // B10b
        if (session.G4Mode != "repoll") return;
        var shot = cell.Shots.LastOrDefault();
        int ordinal = cell.Shots.Count;
        var view = ResidentView(resident, scene);
        if (g4Vram.Count == 0)
        {
            session.Emit("g4-pass", cell, shot, new { ordinal, phase = "after", view });
            return;
        }
        var manifest = ResidentManifest(scene);
        var evicted = manifest.Where(e => PeekResident(resident, e.DiskPath) != e.Handle).Select(e => new
        {
            e.Category, e.GamePath, e.DiskPath, e.Handle, residentPeek = PeekResident(resident, e.DiskPath),
            live = e.Handle > 0 && GL.IsTexture(e.Handle)
        }).ToArray();
        int freeBeforeRelease = FreeVramKb();
        foreach (int tex in g4Vram) GL.DeleteTexture(tex);
        g4Vram.Clear();
        GL.Finish();
        session.Emit("g4-repoll", cell, shot, new
        {
            ordinal, view, retainedEntries = manifest.Length, evictedCount = evicted.Length,
            evictedLiveCount = evicted.Count(x => x.live), evicted = evicted.Take(60).ToArray(),
            freeKbBeforeRelease = freeBeforeRelease, freeKbAfterRelease = FreeVramKb(), ballastBytes = g4VramBytes
        });
    }

    /// <summary>G4 OOM arm: before the first install load of the requested category that must upload (not resident, not
    /// in the manager's private map), queue a real driver GL_OUT_OF_MEMORY from an impossible scratch buffer allocation,
    /// first confirmed by reading it once, then raised again and left for the dependency's single GetError.</summary>
    internal OomProbe? BeforeInstallLoad(string category, string gamePath, string? diskPath, GlTextureManager manager,
        ResidentTextureCache resident)
    {
        ThrowIfDisposed();
        if (session.G4Mode != "oom" || g4OomFired) return null;
        if (!StringComparer.Ordinal.Equals(category, session.G4OomCategory) || string.IsNullOrEmpty(diskPath)) return null;
        if (PeekResident(resident, diskPath) != -1 || PeekPrivate(manager, gamePath) != -1) return null;
        g4OomFired = true;
        var drained = new List<string>();
        for (int i = 0; i < 32; i++)
        {
            var e = GL.GetError();
            if (e == ErrorCode.NoError) break;
            drained.Add(e.ToString());
        }
        long budgetBefore = resident.BudgetBytes;
        object? ceilingBefore = (object?)resident.OomCeilingBytes;
        int prior = GL.GetInteger(GetPName.ArrayBufferBinding);
        int buf = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, buf);
        GL.BufferData(BufferTarget.ArrayBuffer, new IntPtr(1L << 40), IntPtr.Zero, BufferUsageHint.StaticDraw);   // 1 TiB
        var probe = GL.GetError();
        if (probe == ErrorCode.OutOfMemory)
            GL.BufferData(BufferTarget.ArrayBuffer, new IntPtr(1L << 40), IntPtr.Zero, BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ArrayBuffer, prior);
        GL.DeleteBuffer(buf);
        var row = new OomProbe(category, gamePath, diskPath!, drained.ToArray(), probe.ToString(), budgetBefore, ceilingBefore,
            manager.WhiteTexture, session.OrderMark());
        session.Emit("g4-oom-injected", cell, cell.Shots.LastOrDefault(), row);
        return probe == ErrorCode.OutOfMemory ? row : null;
    }

    internal void AfterInstallLoad(OomProbe probe, int loaded, bool missing, GlTextureManager manager, ResidentTextureCache resident)
    {
        ThrowIfDisposed();
        var pending = GL.GetError();   // NoError when the dependency's own GetError consumed the queued failure
        int privatePeek = PeekPrivate(manager, probe.GamePath);
        session.Emit("g4-oom-observed", cell, cell.Shots.LastOrDefault(), new
        {
            probe.Category, probe.GamePath, probe.DiskPath, loaded, white = manager.WhiteTexture,
            returnedWhite = loaded == manager.WhiteTexture, calledMissing = missing,
            missingSetCount = manager.MissingTexturePaths.Count,
            missingSetHasPath = manager.MissingTexturePaths.Any(x => StringComparer.OrdinalIgnoreCase.Equals(x, probe.GamePath)),
            budgetBefore = probe.BudgetBefore, budgetAfter = resident.BudgetBytes, ceilingBefore = probe.CeilingBefore,
            ceilingAfter = (object?)resident.OomCeilingBytes, residentPeek = PeekResident(resident, probe.DiskPath),
            privateCachePeek = privatePeek, privateCachedWhite = privatePeek == manager.WhiteTexture,
            pendingErrorAfter = pending.ToString()
        });
    }

    /// <summary>G4 OOM arm, per-shot path (no resident cache): the same queued real GL_OUT_OF_MEMORY before the first
    /// load of the requested category that is not already in the shot manager's private map.</summary>
    internal OomProbe? BeforeOrdinaryLoad(string category, string gamePath, string? diskPath, GlTextureManager manager)
    {
        ThrowIfDisposed();
        if (session.G4Mode != "oom" || g4OomFired) return null;
        if (!StringComparer.Ordinal.Equals(category, session.G4OomCategory) || PeekPrivate(manager, gamePath) != -1) return null;
        g4OomFired = true;
        var drained = new List<string>();
        for (int i = 0; i < 32; i++)
        {
            var e = GL.GetError();
            if (e == ErrorCode.NoError) break;
            drained.Add(e.ToString());
        }
        int prior = GL.GetInteger(GetPName.ArrayBufferBinding);
        int buf = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, buf);
        GL.BufferData(BufferTarget.ArrayBuffer, new IntPtr(1L << 40), IntPtr.Zero, BufferUsageHint.StaticDraw);   // 1 TiB
        var probe = GL.GetError();
        if (probe == ErrorCode.OutOfMemory)
            GL.BufferData(BufferTarget.ArrayBuffer, new IntPtr(1L << 40), IntPtr.Zero, BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ArrayBuffer, prior);
        GL.DeleteBuffer(buf);
        var row = new OomProbe(category, gamePath, diskPath ?? "", drained.ToArray(), probe.ToString(), -1, null,
            manager.WhiteTexture, session.OrderMark());
        session.Emit("g4-oom-injected", cell, cell.Shots.LastOrDefault(), new { path = "per-shot", probe = row });
        return probe == ErrorCode.OutOfMemory ? row : null;
    }

    /// <summary>G4 OOM arm, per-shot path: what the load returned and every error still queued after it (read here, so the
    /// record is the last reader).</summary>
    internal void AfterOrdinaryLoad(OomProbe probe, int loaded, GlTextureManager manager)
    {
        ThrowIfDisposed();
        var pending = new List<string>();
        for (int i = 0; i < 32; i++)
        {
            var e = GL.GetError();
            if (e == ErrorCode.NoError) break;
            pending.Add(e.ToString());
        }
        session.Emit("g4-oom-observed", cell, cell.Shots.LastOrDefault(), new
        {
            path = "per-shot", probe.Category, probe.GamePath, loaded, white = manager.WhiteTexture,
            returnedWhite = loaded == manager.WhiteTexture, liveTexture = loaded > 0 && GL.IsTexture(loaded),
            calledMissing = manager.MissingTexturePaths.Any(x => StringComparer.OrdinalIgnoreCase.Equals(x, probe.GamePath)),
            pendingErrorsAfter = pending.ToArray(), pendingErrorAfter = pending.Count == 0 ? "NoError" : pending[0]
        });
    }

    // ---- G5: poison before a shot, the state immediately before its first draw, and the shot inputs ----

    static string B(bool v) => v ? "true" : "false";
    static string F(float v) => v.ToString("R", Inv);
    static string V3(OpenTK.Mathematics.Vector3 v) => F(v.X) + "," + F(v.Y) + "," + F(v.Z);
    static string V4(OpenTK.Mathematics.Vector4 v) => F(v.X) + "," + F(v.Y) + "," + F(v.Z) + "," + F(v.W);
    static string N3(System.Numerics.Vector3 v) => F(v.X) + "," + F(v.Y) + "," + F(v.Z);   // GlMesh uses System.Numerics
    static string I(int v) => v.ToString(Inv);
    static string Digest(IEnumerable<string> values) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", values))))[..16];

    Dictionary<string, string> GlState()
    {
        var x = new Dictionary<string, string>(StringComparer.Ordinal);
        string Role(int h, int poison, string name) => h == 0 ? "0" : h == poison ? name : "other";
        x["gl.drawFramebuffer"] = Role(GL.GetInteger(GetPName.DrawFramebufferBinding), g45Fbo, "poison-fbo");
        x["gl.readFramebuffer"] = Role(GL.GetInteger(GetPName.ReadFramebufferBinding), g45Fbo, "poison-fbo");
        var vp = new int[4]; GL.GetInteger(GetPName.Viewport, vp); x["gl.viewport"] = string.Join(",", vp.Select(I));
        var cc = new float[4]; GL.GetFloat(GetPName.ColorClearValue, cc); x["gl.clearColor"] = string.Join(",", cc.Select(F));
        x["gl.depthTest"] = B(GL.IsEnabled(EnableCap.DepthTest));
        x["gl.depthFunc"] = I(GL.GetInteger(GetPName.DepthFunc));
        x["gl.depthMask"] = B(GL.GetBoolean(GetPName.DepthWritemask));
        x["gl.cull"] = B(GL.IsEnabled(EnableCap.CullFace));
        x["gl.cullMode"] = I(GL.GetInteger(GetPName.CullFaceMode));
        x["gl.frontFace"] = I(GL.GetInteger(GetPName.FrontFace));
        x["gl.blend"] = B(GL.IsEnabled(EnableCap.Blend));
        x["gl.blendFunc"] = I(GL.GetInteger(GetPName.BlendSrcRgb)) + "," + I(GL.GetInteger(GetPName.BlendDstRgb)) + ","
                          + I(GL.GetInteger(GetPName.BlendSrcAlpha)) + "," + I(GL.GetInteger(GetPName.BlendDstAlpha));
        x["gl.blendEq"] = I(GL.GetInteger(GetPName.BlendEquationRgb)) + "," + I(GL.GetInteger(GetPName.BlendEquationAlpha));
        x["gl.multisample"] = B(GL.IsEnabled(EnableCap.Multisample));
        x["gl.alphaToCoverage"] = B(GL.IsEnabled(EnableCap.SampleAlphaToCoverage));
        x["gl.sampleCoverage"] = B(GL.IsEnabled(EnableCap.SampleCoverage));
        x["gl.clip0"] = B(GL.IsEnabled(EnableCap.ClipDistance0));
        x["gl.activeTexture"] = I(GL.GetInteger(GetPName.ActiveTexture));
        x["gl.program"] = Role(GL.GetInteger(GetPName.CurrentProgram), g45Program, "poison-program");
        int vao = GL.GetInteger(GetPName.VertexArrayBinding);
        x["gl.vao"] = Role(vao, g45Vao, "poison-vao");
        if (vao != 0)
        {
            string attribs = "";
            for (int i = 0; i < 8; i++)
            {
                GL.GetVertexAttrib(i, VertexAttribParameter.ArrayEnabled, out int enabled);
                attribs += enabled != 0 ? "1" : "0";
            }
            x["gl.attribs"] = attribs;
        }
        else x["gl.attribs"] = "vao0";
        x["gl.scissor"] = B(GL.IsEnabled(EnableCap.ScissorTest));
        var sb = new int[4]; GL.GetInteger(GetPName.ScissorBox, sb); x["gl.scissorBox"] = string.Join(",", sb.Select(I));
        var cm = new bool[4]; GL.GetBoolean(GetPName.ColorWritemask, cm); x["gl.colorMask"] = string.Concat(cm.Select(b => b ? "1" : "0"));
        x["gl.stencil"] = B(GL.IsEnabled(EnableCap.StencilTest));
        var dr = new float[2]; GL.GetFloat(GetPName.DepthRange, dr); x["gl.depthRange"] = string.Join(",", dr.Select(F));
        x["gl.polyOffsetFill"] = B(GL.IsEnabled(EnableCap.PolygonOffsetFill));
        x["gl.polyOffset"] = F(GL.GetFloat(GetPName.PolygonOffsetFactor)) + "," + F(GL.GetFloat(GetPName.PolygonOffsetUnits));
        x["gl.srgb"] = B(GL.IsEnabled(EnableCap.FramebufferSrgb));
        return x;
    }

    static Dictionary<string, string> RendererState(GlRenderer? r)
    {
        var x = new Dictionary<string, string>(StringComparer.Ordinal);
        if (r is null) return x;
        x["r.bloom"] = B(r.EnableBloom); x["r.shadows"] = B(r.EnableShadows); x["r.ao"] = B(r.EnableAmbientOcclusion);
        x["r.clearColor"] = V3(r.ClearColor);
        for (int i = 0; i < r.Lights.Length; i++)
        {
            var l = r.Lights[i];
            x["r.light" + I(i)] = I(l.Type) + "|" + V3(l.Direction) + "|" + V3(l.Color) + "|" + F(l.Intensity);
        }
        x["r.exposure"] = F(r.Exposure); x["r.tonemap"] = B(r.EnableToneMapping); x["r.vignette"] = F(r.VignetteIntensity);
        x["r.modelScale"] = F(r.ModelScale); x["r.backlight"] = V3(r.BacklightColor);
        x["r.clipEnabled"] = B(r.ClipEnabled); x["r.clipPlane"] = V4(r.ClipPlane);
        return x;
    }

    static string[] G5MeshFields() =>
    [
        "IsRendering", "HasEmissive", "HasGlowMap", "EmissiveEnabled", "EmissiveColor", "EmissiveMultiple", "UseAlphaTest",
        "AlphaThreshold", "HasAlphaBlend", "IsDoubleSided", "MaterialAlpha", "DepthWrite", "IsDecal", "SrcBlendIndex",
        "DstBlendIndex", "IsModelSpace", "HasVertexColors", "HasNormalMap", "DiffuseTexture", "NormalTexture",
    ];

    /// <summary>The value each mesh poison writes, in the form the state readback prints it.</summary>
    static string G5MeshPoison(string field) => field switch
    {
        "IsRendering" => "false", "HasEmissive" => "true", "HasGlowMap" => "true", "EmissiveEnabled" => "false",
        "EmissiveColor" => N3(new(0.913f, 0.071f, 0.829f)), "EmissiveMultiple" => F(1.537f), "UseAlphaTest" => "true",
        "AlphaThreshold" => F(0.771f), "HasAlphaBlend" => "true", "IsDoubleSided" => "true", "MaterialAlpha" => F(0.333f),
        "DepthWrite" => "false", "IsDecal" => "true", "SrcBlendIndex" => "1", "DstBlendIndex" => "1", "IsModelSpace" => "true",
        "HasVertexColors" => "true", "HasNormalMap" => "true", "DiffuseTexture" => "white", "NormalTexture" => "white",
        _ => throw new ArgumentException(field)
    };

    static string FieldValue(GlMesh m, string field, Dictionary<int, string> labels) => field switch
    {
        "IsRendering" => B(m.IsRendering), "HasEmissive" => B(m.HasEmissive), "HasGlowMap" => B(m.HasGlowMap),
        "EmissiveEnabled" => B(m.EmissiveEnabled), "EmissiveColor" => N3(m.EmissiveColor), "EmissiveMultiple" => F(m.EmissiveMultiple),
        "UseAlphaTest" => B(m.UseAlphaTest), "AlphaThreshold" => F(m.AlphaThreshold), "HasAlphaBlend" => B(m.HasAlphaBlend),
        "IsDoubleSided" => B(m.IsDoubleSided), "MaterialAlpha" => F(m.MaterialAlpha), "DepthWrite" => B(m.DepthWrite),
        "IsDecal" => B(m.IsDecal), "SrcBlendIndex" => I(m.SrcBlendIndex), "DstBlendIndex" => I(m.DstBlendIndex),
        "IsModelSpace" => B(m.IsModelSpace), "HasVertexColors" => B(m.HasVertexColors), "HasNormalMap" => B(m.HasNormalMap),
        "DiffuseTexture" => labels.TryGetValue(m.DiffuseTexture, out var d) ? d : "other",
        "NormalTexture" => labels.TryGetValue(m.NormalTexture, out var n) ? n : "other",
        _ => throw new ArgumentException(field)
    };

    static Dictionary<int, string> TextureLabels(BuiltScene scene, GlTextureManager? install)
    {
        var labels = new Dictionary<int, string> { [0] = "0" };
        if (install is not null) labels[install.WhiteTexture] = "white";
        foreach (var t in scene.TextureManifest) labels.TryAdd(t.Handle, "tex:" + t.GamePath.ToLowerInvariant());
        foreach (var t in scene.OwnedTextureHandles) labels.TryAdd(t.Handle, "owned:" + t.Category);
        return labels;
    }

    static List<GlMesh> StaticMeshes(GlRenderer renderer, BuiltScene scene)
    {
        var vaos = scene.MeshHandles.Select(h => h.Vao).ToHashSet();
        return renderer.Meshes.Where(m => vaos.Contains(m.Vao)).ToList();
    }

    static Dictionary<string, string> MeshState(GlRenderer? renderer, BuiltScene? scene, GlTextureManager? install)
    {
        var x = new Dictionary<string, string>(StringComparer.Ordinal);
        if (renderer is null || scene is null) return x;
        var vaos = scene.MeshHandles.Select(h => h.Vao).ToHashSet();
        var labels = TextureLabels(scene, install);
        foreach (var (name, list) in new[] { ("static", renderer.Meshes.Where(m => vaos.Contains(m.Vao)).ToList()),
                                            ("dynamic", renderer.Meshes.Where(m => !vaos.Contains(m.Vao)).ToList()) })
        {
            x[$"mesh.{name}.count"] = I(list.Count);
            foreach (string f in G5MeshFields())
            {
                var values = list.Select(m => FieldValue(m, f, labels)).ToArray();
                x[$"mesh.{name}.{f}"] = Digest(values);
                string poison = G5MeshPoison(f);
                x[$"mesh.{name}.{f}.eqPoison"] = I(values.Count(v => v == poison));
            }
        }
        return x;
    }

    void EnsurePoisonObjects()
    {
        if (g45Fbo == 0)
        {
            int prevTex = GL.GetInteger(GetPName.TextureBinding2D);
            g45FboTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, g45FboTexture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 8, 8, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.BindTexture(TextureTarget.Texture2D, prevTex);
            int prevDraw = GL.GetInteger(GetPName.DrawFramebufferBinding), prevRead = GL.GetInteger(GetPName.ReadFramebufferBinding);
            g45Fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, g45Fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, g45FboTexture, 0);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, prevDraw);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, prevRead);
        }
        if (g45Program == 0)
        {
            int vs = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vs, "#version 330 core\nvoid main() { gl_Position = vec4(0.0, 0.0, 0.0, 1.0); }\n");
            GL.CompileShader(vs);
            int fs = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fs, "#version 330 core\nout vec4 c;\nvoid main() { c = vec4(1.0, 0.0, 1.0, 1.0); }\n");
            GL.CompileShader(fs);
            g45Program = GL.CreateProgram();
            GL.AttachShader(g45Program, vs);
            GL.AttachShader(g45Program, fs);
            GL.LinkProgram(g45Program);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
        }
        if (g45Vao == 0)
        {
            int prevVao = GL.GetInteger(GetPName.VertexArrayBinding);
            g45Vao = GL.GenVertexArray();
            GL.BindVertexArray(g45Vao);
            for (int i = 0; i < 8; i++) GL.EnableVertexAttribArray(i);
            GL.BindVertexArray(prevVao);
        }
    }

    /// <summary>Applies one poison (unless it is the requested no-op) and returns the readback keys and values that
    /// confirm it.</summary>
    Dictionary<string, string> ApplyPoison(string item, GlRenderer renderer, BuiltScene scene, GlTextureManager install, bool noop)
    {
        var x = new Dictionary<string, string>(StringComparer.Ordinal);
        var statics = StaticMeshes(renderer, scene);
        void Mesh(string field, Action<GlMesh> set)
        {
            if (!noop) foreach (var m in statics) set(m);
            x[$"mesh.static.{field}.eqPoison"] = I(statics.Count);
        }
        void R(string key, string value, Action set) { if (!noop) set(); x[key] = value; }
        switch (item)
        {
            case "mesh-visibility": Mesh("IsRendering", m => m.IsRendering = false); break;
            case "mesh-emissive-tint":
                Mesh("HasEmissive", m => m.HasEmissive = true);
                Mesh("EmissiveColor", m => m.EmissiveColor = new(0.913f, 0.071f, 0.829f));
                Mesh("EmissiveMultiple", m => m.EmissiveMultiple = 1.537f);
                break;
            case "mesh-emissive-flags":
                Mesh("HasGlowMap", m => m.HasGlowMap = true);
                Mesh("EmissiveEnabled", m => m.EmissiveEnabled = false);
                break;
            case "mesh-material":
                Mesh("UseAlphaTest", m => m.UseAlphaTest = true); Mesh("AlphaThreshold", m => m.AlphaThreshold = 0.771f);
                Mesh("HasAlphaBlend", m => m.HasAlphaBlend = true); Mesh("IsDoubleSided", m => m.IsDoubleSided = true);
                Mesh("MaterialAlpha", m => m.MaterialAlpha = 0.333f); Mesh("DepthWrite", m => m.DepthWrite = false);
                Mesh("IsDecal", m => m.IsDecal = true); Mesh("SrcBlendIndex", m => m.SrcBlendIndex = 1);
                Mesh("DstBlendIndex", m => m.DstBlendIndex = 1); Mesh("IsModelSpace", m => m.IsModelSpace = true);
                Mesh("HasVertexColors", m => m.HasVertexColors = true); Mesh("HasNormalMap", m => m.HasNormalMap = true);
                Mesh("DiffuseTexture", m => m.DiffuseTexture = install.WhiteTexture);
                Mesh("NormalTexture", m => m.NormalTexture = install.WhiteTexture);
                break;
            case "renderer-core":
                R("r.bloom", "true", () => renderer.EnableBloom = true);
                R("r.shadows", "true", () => renderer.EnableShadows = true);
                R("r.ao", "true", () => renderer.EnableAmbientOcclusion = true);
                R("r.clearColor", V3(new(0.913f, 0.071f, 0.829f)), () => renderer.ClearColor = new(0.913f, 0.071f, 0.829f));
                break;
            case "renderer-lights":
                for (int i = 0; i < renderer.Lights.Length; i++)
                {
                    int slot = i;
                    var poison = new GlRenderer.LightData { Type = 2, Direction = new(0.31f, -0.83f, 0.47f),
                        Color = new(0.9f, 0.21f + 0.01f * slot, 0.1f), Intensity = 0.61f + 0.01f * slot };
                    R("r.light" + I(slot), I(poison.Type) + "|" + V3(poison.Direction) + "|" + V3(poison.Color) + "|" + F(poison.Intensity),
                        () => renderer.Lights[slot] = poison);
                }
                break;
            case "renderer-extra":
                R("r.exposure", F(2.5f), () => renderer.Exposure = 2.5f);
                R("r.tonemap", "true", () => renderer.EnableToneMapping = true);
                R("r.vignette", F(0.8f), () => renderer.VignetteIntensity = 0.8f);
                R("r.modelScale", F(1.25f), () => renderer.ModelScale = 1.25f);
                R("r.backlight", V3(new(1f, 0f, 0f)), () => renderer.BacklightColor = new(1f, 0f, 0f));
                R("r.clipEnabled", "true", () => renderer.ClipEnabled = true);
                R("r.clipPlane", V4(new(0f, 0f, 1f, 0f)), () => renderer.ClipPlane = new(0f, 0f, 1f, 0f));
                break;
            case "gl-framebuffer":
                EnsurePoisonObjects();
                R("gl.drawFramebuffer", "poison-fbo", () => GL.BindFramebuffer(FramebufferTarget.Framebuffer, g45Fbo));
                x["gl.readFramebuffer"] = "poison-fbo";
                break;
            case "gl-viewport": R("gl.viewport", "3,5,17,11", () => GL.Viewport(3, 5, 17, 11)); break;
            case "gl-clearcolor":
                R("gl.clearColor", F(0.913f) + "," + F(0.071f) + "," + F(0.829f) + "," + F(0.5f), () => GL.ClearColor(0.913f, 0.071f, 0.829f, 0.5f));
                break;
            case "gl-depth-test": R("gl.depthTest", "false", () => GL.Disable(EnableCap.DepthTest)); break;
            case "gl-depth-func": R("gl.depthFunc", I((int)DepthFunction.Greater), () => GL.DepthFunc(DepthFunction.Greater)); break;
            case "gl-depth-write": R("gl.depthMask", "false", () => GL.DepthMask(false)); break;
            case "gl-cull":
                R("gl.cull", "true", () => GL.Enable(EnableCap.CullFace));
                R("gl.cullMode", I((int)CullFaceMode.Front), () => GL.CullFace(CullFaceMode.Front));
                break;
            case "gl-frontface": R("gl.frontFace", I((int)FrontFaceDirection.Cw), () => GL.FrontFace(FrontFaceDirection.Cw)); break;
            case "gl-blend": R("gl.blend", "true", () => GL.Enable(EnableCap.Blend)); break;
            case "gl-blendfunc":
                R("gl.blendFunc", "1,1,1,1", () => GL.BlendFunc(BlendingFactor.One, BlendingFactor.One));
                break;
            case "gl-multisample": R("gl.multisample", "false", () => GL.Disable(EnableCap.Multisample)); break;
            case "gl-a2c": R("gl.alphaToCoverage", "true", () => GL.Enable(EnableCap.SampleAlphaToCoverage)); break;
            case "gl-clip": R("gl.clip0", "true", () => GL.Enable(EnableCap.ClipDistance0)); break;
            case "gl-program":
                EnsurePoisonObjects();
                R("gl.program", "poison-program", () => GL.UseProgram(g45Program));
                break;
            case "gl-vao-attribs":
                EnsurePoisonObjects();
                R("gl.vao", "poison-vao", () => GL.BindVertexArray(g45Vao));
                x["gl.attribs"] = "11111111";
                break;
            case "gl-activetexture": R("gl.activeTexture", I((int)TextureUnit.Texture7), () => GL.ActiveTexture(TextureUnit.Texture7)); break;
            case "gl-scissor":
                R("gl.scissor", "true", () => GL.Enable(EnableCap.ScissorTest));
                R("gl.scissorBox", "0,0,7,7", () => GL.Scissor(0, 0, 7, 7));
                break;
            case "gl-colormask": R("gl.colorMask", "1011", () => GL.ColorMask(true, false, true, true)); break;
            case "gl-stencil": R("gl.stencil", "true", () => GL.Enable(EnableCap.StencilTest)); break;
            case "gl-depthrange": R("gl.depthRange", F(0.5f) + "," + F(1f), () => GL.DepthRange(0.5, 1.0)); break;
            case "gl-blendeq":
                R("gl.blendEq", I((int)BlendEquationMode.FuncReverseSubtract) + "," + I((int)BlendEquationMode.FuncReverseSubtract),
                    () => GL.BlendEquation(BlendEquationMode.FuncReverseSubtract));
                break;
            case "gl-polyoffset":
                R("gl.polyOffsetFill", "true", () => GL.Enable(EnableCap.PolygonOffsetFill));
                R("gl.polyOffset", F(4f) + "," + F(4f), () => GL.PolygonOffset(4f, 4f));
                break;
            case "gl-srgb": R("gl.srgb", "true", () => GL.Enable(EnableCap.FramebufferSrgb)); break;
            default: throw new ArgumentException("unknown G5 poison item " + item);
        }
        return x;
    }

    /// <summary>G5: before the requested shot (default the second of the cell), after the previous shot's teardown and
    /// before any of this shot's resets, poison the retained meshes, the renderer and the GL state, and confirm every
    /// poison by reading it back (a poison that does not read back, or that equals the prior value, is a setup failure
    /// for the driver, never a pass).</summary>
    internal void PoisonBeforeShot(GlRenderer renderer, BuiltScene scene, GlTextureManager install)
    {
        ThrowIfDisposed();
        if (session.G5Poison.Count == 0 || cell.Shots.Count != session.G5Shot) return;
        var shot = cell.Shots.LastOrDefault();
        Dictionary<string, string> All(Dictionary<string, string> a, Dictionary<string, string> b, Dictionary<string, string> c)
        {
            var m = new Dictionary<string, string>(a, StringComparer.Ordinal);
            foreach (var kv in b) m[kv.Key] = kv.Value;
            foreach (var kv in c) m[kv.Key] = kv.Value;
            return m;
        }
        var before = All(GlState(), RendererState(renderer), MeshState(renderer, scene, install));
        var applied = new List<(string Item, bool NoOp, Dictionary<string, string> Expected)>();
        foreach (string item in session.G5Poison)
        {
            bool noop = StringComparer.Ordinal.Equals(item, session.G5NoOp);
            applied.Add((item, noop, ApplyPoison(item, renderer, scene, install, noop)));
        }
        var after = All(GlState(), RendererState(renderer), MeshState(renderer, scene, install));
        var rows = applied.Select(a => new
        {
            item = a.Item, noop = a.NoOp,
            confirmed = a.Expected.All(kv => after.TryGetValue(kv.Key, out var v) && v == kv.Value)
                        && a.Expected.Any(kv => !before.TryGetValue(kv.Key, out var v) || v != kv.Value),
            expected = a.Expected,
            readBack = a.Expected.Keys.ToDictionary(k => k, k => after.TryGetValue(k, out var v) ? v : null),
            prior = a.Expected.Keys.ToDictionary(k => k, k => before.TryGetValue(k, out var v) ? v : null)
        }).ToArray();
        session.Emit("g5-poison", cell, shot, new
        {
            ordinal = cell.Shots.Count, items = rows.Length, confirmedCount = rows.Count(r => r.confirmed), rows, before, after
        });
    }

    /// <summary>G5: the state at the first draw of every shot (retained or isolated), just before GlRenderer.Render.</summary>
    internal void PreDrawState(GlRenderer? renderer, BuiltScene scene, GlTextureManager? install)
    {
        ThrowIfDisposed();
        if (!session.G5Observe) return;
        var shot = cell.Shots.LastOrDefault();
        if (shot is null || !g45PreDrawShots.Add(shot.ShotId)) return;
        var state = GlState();
        foreach (var kv in RendererState(renderer)) state[kv.Key] = kv.Value;
        foreach (var kv in MeshState(renderer, scene, install)) state[kv.Key] = kv.Value;
        session.Emit("g5-predraw", cell, shot, new { ordinal = cell.Shots.Count, state });
    }

    static string? Fmt(object? v) => v switch
    {
        null => null, string s => s, bool b => B(b), float f => F(f), int i => I(i),
        System.Numerics.Vector3 n => F(n.X) + "," + F(n.Y) + "," + F(n.Z),
        _ => Convert.ToString(v, Inv)
    };

    /// <summary>G5: a worklist shot's inputs, before the per-shot resets ("pre-reset": what the previous shot or the
    /// command line left, i.e. the poison) and after them and the row's overrides ("post-reset"), before preparation.</summary>
    internal void ShotInputs(int wlCell, int wlShot, string phase, IReadOnlyDictionary<string, object?> values)
    {
        ThrowIfDisposed();
        if (!session.G5Observe) return;
        session.Emit("g5-shot-inputs", cell, null,
            new { wlCell, wlShot, phase, values = values.ToDictionary(k => k.Key, k => Fmt(k.Value)) });
    }

    /// <summary>G5: the camera the shot actually resolved (eye, aim point, fov), before preparation and draw.</summary>
    internal void ShotCamera(IReadOnlyDictionary<string, object?> values)
    {
        ThrowIfDisposed();
        if (!session.G5Observe) return;
        session.Emit("g5-shot-camera", cell, null, new { values = values.ToDictionary(k => k.Key, k => Fmt(k.Value)) });
    }

    // ---- B10b (launch 10, part 1b RED): the resident cache at every render pass and at cell end. Measurement only, inert
    // without a RED session; reads the dependency's fields without GL calls. The ceiling arm's one reflection write is
    // labelled injected in its own event. ----
    static object ResidentFields(ResidentTextureCache resident) => new
    {
        budgetBytes = resident.BudgetBytes, currentBytes = resident.CurrentBytes, count = resident.Count,
        rendersSinceRepoll = (object?)resident.RendersSinceRepoll, epoch = (object?)resident.Epoch,
        oomCeilingBytes = (object?)resident.OomCeilingBytes,
        overrideBudgetBytes = (object?)resident.OverrideBudgetBytes,
        vramQueryAvailable = (object?)resident.VramQueryAvailable,
    };

    bool b10bCeilingFired;

    /// <summary>B10b passes and ceiling arms: the resident cache just before and just after every BeginRenderPass. In the
    /// ceiling arm, immediately before the pass whose BeginRenderPass is a repoll (the counter reads 15), lower the
    /// dependency's OOM ceiling to the requested value, so that the dependency's own repoll moves the budget. On a GPU
    /// whose free memory keeps the dynamic budget at its hard maximum, a repoll otherwise recomputes the same value.</summary>
    void PassRecord(ResidentTextureCache resident, string phase)
    {
        var shot = cell.Shots.LastOrDefault();
        int ordinal = cell.Shots.Count;
        session.Emit("b10b-pass", cell, shot, new { ordinal, phase, fields = ResidentFields(resident) });
        if (phase != "before" || session.G4Mode != "ceiling" || b10bCeilingFired) return;
        if (resident.RendersSinceRepoll != 15) return;
        var field = typeof(ResidentTextureCache).GetField("_oomCeilingBytes", Private)
            ?? throw new InvalidOperationException("B10b ceiling arm: ResidentTextureCache._oomCeilingBytes is absent");
        object? before = field.GetValue(resident);
        field.SetValue(resident, session.G4CeilingBytes!.Value);
        b10bCeilingFired = true;
        session.Emit("b10b-ceiling-injected", cell, shot, new
        {
            ordinal, injected = true, ceilingBefore = before, ceilingAfter = field.GetValue(resident),
            budgetBefore = resident.BudgetBytes, nextPassIsRepoll = true
        });
    }

    /// <summary>B10b cell-level fault point (test-only, observer revision 02): called after a cell's shot loop, inside the
    /// cell's try, so a fault here leaves the shot loop and reaches the cell-level test boundary. Active only with
    /// VP_GRAPHICS_REUSE_RED_FAULT=after-shots; otherwise it returns without recording anything.</summary>
    internal void AfterShotLoop()
    {
        ThrowIfDisposed();
        if (!StringComparer.Ordinal.Equals(session.RequestedFault, "after-shots")) return;
        session.Emit("checkpoint", cell, null, new { boundary = "after-shots" });
        session.RequestedFaultReached = true;
        throw new ExpectedGraphicsFault("after-shots", cell.CellId, "cell");
    }

    /// <summary>B10b oracle: the resident cache at cell end, read by the observer independently of any telemetry hook.
    /// Called first thing in GraphicsCell.Dispose, in every RED session.</summary>
    internal void ResidentOracle(ResidentTextureCache? resident)
    {
        ThrowIfDisposed();
        session.Emit("b10b-resident-cell-end", cell, cell.Shots.LastOrDefault(), new
        {
            present = resident is not null, fields = resident is null ? null : ResidentFields(resident)
        });
    }

    /// <summary>G4/G5 cell end (context current): delete the scratch objects the hooks created and any device memory a
    /// repoll arm still holds. Ballast added to the resident cache is the cache's and dies with it.</summary>
    internal void G45CellEnd()
    {
        if (g4Vram.Count > 0) { foreach (int tex in g4Vram) GL.DeleteTexture(tex); g4Vram.Clear(); }
        if (g45Program != 0) { GL.DeleteProgram(g45Program); g45Program = 0; }
        if (g45Vao != 0) { GL.DeleteVertexArray(g45Vao); g45Vao = 0; }
        if (g45Fbo != 0) { GL.DeleteFramebuffer(g45Fbo); g45Fbo = 0; }
        if (g45FboTexture != 0) { GL.DeleteTexture(g45FboTexture); g45FboTexture = 0; }
    }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
