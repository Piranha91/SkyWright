using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CharacterViewer.Rendering;
using OpenTK.Graphics.OpenGL4;

namespace ViewProbe;

/// <summary>Cell-owned uploaded static geometry and the resident-texture bindings it uses.</summary>
internal sealed class BuiltScene : IDisposable
{
    internal readonly record struct MeshLease(GlMesh Mesh, string InputSha256, bool CreatedNow);
    internal readonly record struct TextureLease(int Handle, string[] MissingPaths);
    internal readonly record struct ManifestResult(bool Valid, int ExpectedCount, int FoundCount, string? Failure);

    sealed record MeshEntry(string Category, string InputSha256, GlMesh Mesh)
    {
        internal bool AddedToRenderer { get; set; }
        internal bool BaselineRendering { get; set; } = true;
    }
    sealed record TextureEntry(string Category, string GamePath, string? DiskPath, int Handle, bool Resident,
        string[] MissingPaths);

    readonly GlRenderer renderer;
    readonly List<MeshEntry> meshes = [];
    readonly List<TextureEntry> textures = [];
    readonly Dictionary<string, int> ownedTextures = new(StringComparer.Ordinal);
    int meshCursor;
    int textureCursor;
    bool passOpen;
    bool disposed;

    internal string Key { get; }
    internal int MeshCount => meshes.Count;
    internal int ManifestCount => textures.Count(x => x.Resident);
    internal IReadOnlyCollection<string> ResidentDiskPaths => textures
        .Where(x => x.Resident && !string.IsNullOrEmpty(x.DiskPath)).Select(x => x.DiskPath!)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // Measurement only (Gate B4, G2 RED): read-only views and a dispose callback for the private RED observer.
    // The render path never reads them; DisposeObserved stays null without a RED session.
    internal IReadOnlyList<(string Category, int Vao, int Vbo, int Ebo)> MeshHandles =>
        meshes.Select(x => (x.Category, x.Mesh.Vao, x.Mesh.Vbo, x.Mesh.Ebo)).ToArray();
    internal IReadOnlyList<(string Category, int Handle)> OwnedTextureHandles =>
        ownedTextures.Select(x => (x.Key, x.Value)).ToArray();
    internal IReadOnlyList<(string Category, string GamePath, string? DiskPath, int Handle, bool Resident)> TextureManifest =>
        textures.Select(x => (x.Category, x.GamePath, x.DiskPath, x.Handle, x.Resident)).ToArray();
    internal Action<BuiltScene, IReadOnlyList<(string Category, int Vao, int Vbo, int Ebo)>,
        IReadOnlyList<(string Category, int Handle)>>? DisposeObserved { get; set; }

    internal BuiltScene(string key, GlRenderer renderer)
    {
        Key = key;
        this.renderer = renderer;
    }

    internal void BeginBindingPass()
    {
        ThrowIfDisposed();
        if (passOpen) throw new InvalidOperationException("static binding pass already open");
        meshCursor = 0;
        textureCursor = 0;
        passOpen = true;
        RestoreBaseline();
    }

    internal MeshLease AcquireMesh(string category, float[] vertices, int[] indices)
    {
        ThrowOpen();
        string hash = HashArrays(vertices, indices);
        if (meshCursor < meshes.Count)
        {
            var existing = meshes[meshCursor++];
            if (!StringComparer.Ordinal.Equals(existing.Category, category)
                || !StringComparer.Ordinal.Equals(existing.InputSha256, hash))
                throw new InvalidOperationException(
                    $"retained static binding drift at {meshCursor - 1}: expected {existing.Category}/{existing.InputSha256}, got {category}/{hash}");
            if (existing.Mesh.Vao <= 0 || !GL.IsVertexArray(existing.Mesh.Vao)
                || !GL.IsBuffer(existing.Mesh.Vbo) || !GL.IsBuffer(existing.Mesh.Ebo))
                throw new InvalidOperationException($"retained static binding is no longer live: {category}/{hash}");
            return new(existing.Mesh, hash, false);
        }

        var mesh = new GlMesh();
        meshes.Add(new(category, hash, mesh));
        meshCursor++;
        return new(mesh, hash, true);
    }

    internal void AddedToRenderer(GlMesh mesh)
    {
        ThrowOpen();
        var entry = meshes.FirstOrDefault(x => ReferenceEquals(x.Mesh, mesh))
            ?? throw new InvalidOperationException("static mesh was not acquired from this scene");
        if (entry.AddedToRenderer) throw new InvalidOperationException("static mesh added twice");
        entry.AddedToRenderer = true;
    }

    internal TextureLease Texture(string category, string gamePath, string? diskPath,
        Func<TextureLease> load, int white)
    {
        ThrowOpen();
        if (textureCursor < textures.Count)
        {
            var existing = textures[textureCursor++];
            if (!StringComparer.Ordinal.Equals(existing.Category, category)
                || !StringComparer.OrdinalIgnoreCase.Equals(existing.GamePath, gamePath)
                || !StringComparer.OrdinalIgnoreCase.Equals(existing.DiskPath, diskPath))
                throw new InvalidOperationException(
                    $"retained texture binding drift at {textureCursor - 1}: expected {existing.Category}/{existing.GamePath}, got {category}/{gamePath}");
            if (existing.Handle <= 0 || !GL.IsTexture(existing.Handle))
                throw new InvalidOperationException($"retained texture handle is no longer live: {category}/{gamePath}");
            return new(existing.Handle, existing.MissingPaths);
        }

        var loaded = load();
        // White is manager-owned and intentionally absent from the resident manifest. An unresolved
        // path has no stable cache key and therefore cannot be promised resident across an epoch.
        bool isResident = loaded.Handle != white && loaded.Handle > 0 && !string.IsNullOrWhiteSpace(diskPath);
        textures.Add(new(category, gamePath, diskPath, loaded.Handle, isResident, loaded.MissingPaths));
        textureCursor++;
        return loaded;
    }

    internal (int Handle, bool CreatedNow) OwnedTexture(string category, Func<int> create)
    {
        ThrowOpen();
        if (ownedTextures.TryGetValue(category, out int existing))
        {
            if (existing <= 0 || !GL.IsTexture(existing))
                throw new InvalidOperationException($"retained owned texture is no longer live: {category}/{existing}");
            return (existing, false);
        }
        int handle = create();
        if (handle <= 0 || !GL.IsTexture(handle))
            throw new InvalidOperationException($"failed to create retained texture: {category}/{handle}");
        ownedTextures.Add(category, handle);
        return (handle, true);
    }

    internal void CompleteBindingPass()
    {
        ThrowOpen();
        passOpen = false;
        if (meshCursor != meshes.Count)
            throw new InvalidOperationException($"static mesh binding count drift: observed {meshCursor}, retained {meshes.Count}");
        if (textureCursor < textures.Count)
            throw new InvalidOperationException($"static texture binding count drift: observed {textureCursor}, retained {textures.Count}");
        foreach (var entry in meshes)
        {
            if (!entry.AddedToRenderer)
                throw new InvalidOperationException($"static mesh was uploaded but never added: {entry.Category}/{entry.InputSha256}");
            entry.BaselineRendering = entry.Mesh.IsRendering;
        }
    }

    internal ManifestResult TouchOrRefuse(ResidentTextureCache resident)
    {
        ThrowIfDisposed();
        int found = 0;
        var manifest = textures.Where(x => x.Resident).ToArray();
        foreach (var entry in manifest)
        {
            int current = resident.TryGet(entry.DiskPath!);
            if (current == entry.Handle && current > 0 && GL.IsTexture(current))
            {
                found++;
                continue;
            }
            // G2 F4 (Gate B4): name the three ways a retained binding fails, instead of calling all of them a change.
            string why = current == -1 ? $"no longer resident (evicted); the manifest holds handle {entry.Handle}"
                : current != entry.Handle ? $"resident binding changed: the manifest holds handle {entry.Handle}, the cache holds {current}"
                : $"handle {entry.Handle} is still mapped but is no longer a live texture";
            return new(false, manifest.Length, found, $"{entry.Category}/{entry.GamePath}: {why}");
        }
        return new(true, manifest.Length, found, null);
    }

    internal void RestoreBaseline()
    {
        ThrowIfDisposed();
        foreach (var entry in meshes) entry.Mesh.IsRendering = entry.BaselineRendering;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        passOpen = false;
        var observed = DisposeObserved;
        var observedMeshes = observed is null ? null : MeshHandles;
        var observedOwned = observed is null ? null : OwnedTextureHandles;
        List<Exception> failures = [];
        foreach (var entry in meshes)
        {
            if (entry.AddedToRenderer)
            {
                try { renderer.RemoveMesh(entry.Mesh); }
                catch (Exception ex) { failures.Add(ex); }
            }
            try { entry.Mesh.Dispose(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        meshes.Clear();
        textures.Clear();
        foreach (int handle in ownedTextures.Values)
            try { GL.DeleteTexture(handle); }
            catch (Exception ex) { failures.Add(ex); }
        ownedTextures.Clear();
        if (observed is not null)
            try { observed(this, observedMeshes!, observedOwned!); }
            catch (Exception ex) { failures.Add(ex); }
        if (failures.Count > 0) throw new AggregateException("retained scene teardown failed", failures);
    }

    void ThrowOpen()
    {
        ThrowIfDisposed();
        if (!passOpen) throw new InvalidOperationException("static binding operation outside a pass");
    }
    void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(BuiltScene));
    }

    internal static string HashArrays(float[] vertices, int[] indices)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes(vertices.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(indices.AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
