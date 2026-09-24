using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CharacterViewer.Rendering;
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

    /// <summary>GREEN owner calls this at cell end, before the scene and resident cache are disposed (Gate B2 review,
    /// C2 as corrected): the cache must hold exactly the retained manifest's distinct resident disk paths and no
    /// shot-local texture. TryGet's recency side effect is harmless here; the cache is disposed next.</summary>
    public void ResidentAtCellEnd(ResidentTextureCache resident, IReadOnlyCollection<string> manifestResidentPaths)
    {
        ThrowIfDisposed();
        int count = resident.Count;
        var distinct = manifestResidentPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        int found = distinct.Count(p => resident.TryGet(p) > 0);
        var shotLocal = cell.ShotLocalDiskPaths.Where(p => !distinct.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();
        var shotLocalInCache = shotLocal.Where(p => resident.TryGet(p) > 0).ToArray();
        cell.ResidentAtCellEnd = new(count, distinct.Length, found, shotLocal.Length, shotLocalInCache,
            count == distinct.Length && found == distinct.Length && shotLocalInCache.Length == 0);
        session.Emit("resident-at-cell-end", cell, null, cell.ResidentAtCellEnd);
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
        var checks = cell.CellResources.Where(r => r.ContextId == contextId)
            .Select(r => ResourceCheck.For(r, current)).ToArray();
        cell.CellTeardownChecks.AddRange(checks);
        cell.CellTeardownContextCurrent = current;
        session.Emit("cell-resources-after-owner-dispose", cell, null,
            new { contextId, contextCurrent = current, glVersion = version, checks });
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
            session.Emit("static-bound-for-shot", cell, shot, binding);
        }

        public void MeshAdded(string category, GlMesh mesh, bool borrowed = false)
        {
            if (borrowed)
            {
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

        public void RawBufferUploaded(string family, int handle, float[] data)
        {
            var upload = new RawUpload(family, HashFloats(data), data.Length * sizeof(float), handle);
            shot.RawUploads.Add(upload);
            session.Emit("raw-buffer-uploaded", cell, shot, upload);
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

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            shot.Ended = true;
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

        internal Session(string outputPath, string? requestedFault)
        {
            OutputPath = outputPath;
            RequestedFault = string.IsNullOrWhiteSpace(requestedFault) ? null : requestedFault;
            if (RequestedFault is not null && RequestedFault is not ("after-static" or "after-dynamic" or "before-encode"))
                throw new ArgumentException("VP_GRAPHICS_REUSE_RED_FAULT must be after-static, after-dynamic, or before-encode");
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
            bool shotCleanup = cells.SelectMany(c => c.Shots).All(s => s.TeardownContextCurrent == true
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
                nextOwners, shotCleanup, cellCleanup, liveBoundaries, water, overlays, npcs, masks);
        }

        static object CellBlock(CellState c) => new
        {
            c.CellId, c.Label, c.Index, c.Disposed, contextIds = c.ContextIds,
            c.ContextOwnerEndCount, c.LastContextOwnerEndOrder, c.CellFinallyOrder,
            cellTeardownContextCurrent = c.CellTeardownContextCurrent,
            cellTeardownChecks = c.CellTeardownChecks, residentAtCellEnd = c.ResidentAtCellEnd,
            shots = c.Shots.Select(s => new { s.ShotId, s.ContextId, s.Facts,
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
    internal sealed record RawUpload(string Family, string Sha256, int Bytes, int Handle);
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
        string NpcStatus, string SubjectMaskStatus);

    static string HashArrays(float[] vertices, int[] indices)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(MemoryMarshal.AsBytes(vertices.AsSpan()));
        h.AppendData(MemoryMarshal.AsBytes(indices.AsSpan()));
        return Convert.ToHexString(h.GetHashAndReset());
    }
    static string HashFloats(float[] values) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
