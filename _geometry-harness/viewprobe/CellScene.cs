using CharacterViewer.Rendering;

namespace ViewProbe;

/// <summary>
/// What the shots of ONE CELL may share — a lease that is on by default for a static worklist
/// (since 2026-09-24; <c>--no-reuse-cell</c> turns it off).
///
/// A worklist run builds its scene once per SHOT: two shots of one cell walk the load order twice,
/// resolve the same assets twice and parse the same meshes twice. The shots of a cell differ only
/// in where the camera stands, and a built scene is camera-independent — checked, not assumed: the
/// reference assembly loop contains no distance, frustum or LOD test, the set of cells walked is
/// chosen by GRID rather than by the eye, and the occlusion cull is computed per draw inside the
/// render. So the work that does not depend on the camera can be done once for the cell.
///
/// THIS OBJECT IS A SCOPE, NOT A CACHE THAT GETS CLEARED. It is constructed for a cell and disposed
/// with it; nothing in it is ever "reset" between cells, because between cells there is no object.
/// That is the whole safety argument: a stale table cannot outlive the cell it was built for, so
/// the hazard the strategy names (a table filled for cell 1 still answering in cell 2) has no
/// lifetime to live in. A disposed lease REFUSES rather than quietly building a fresh one, so a
/// later caller cannot turn the scope back into a process-lifetime cache by accident.
///
/// WHAT MAY NOT GO IN HERE, however cheap it looks:
///  - anything the CAMERA decides. `--cull-above` is baked into the uploaded index arrays (a
///    triangle wholly above the cut is dropped before upload), so uploaded meshes belong to the
///    shot even though the parsed mesh does not;
///  - the two build-side camera dependencies the invalidation map names: the route's ground z,
///    which falls back to the eye's height, and `--npc-puck auto`, which reads the camera pitch;
///  - anything whose inputs a shot may change. A shot of a worklist may override the navmesh mode,
///    the cut height, the planes layer, the occlusion mode and the canvas aspect;
///  - anything holding a GL handle. The hidden window, and with it the GL context, is created and
///    destroyed PER SHOT, so an object that owns a texture or a buffer would hand the next shot a
///    handle from a context that no longer exists.
///
/// With <c>--no-reuse-cell</c> the lease is null and every shot builds its own, exactly as before
/// stage 4. The suite (`tools/rendertwin.py`) is what says whether a slice may stay: it renders a worklist as one process and each shot again alone, and compares the
/// pixels, the report, the console and the census.
/// </summary>
public sealed class CellScene : IDisposable
{
    /// <summary>The cell this lease was constructed for — it exists for exactly that long.</summary>
    public string Cell { get; }

    CellAssets? _assets;
    CellWalkFacts? _facts;
    string? _factsKey;
    bool _done;

    public CellScene(string cell)
    {
        Cell = cell;
        // ONE line per lease, not per shot: a per-shot line would differ between a batched shot and
        // the same shot rendered alone, and the equivalence suite compares those two consoles.
        Console.WriteLine("   cell scene: ON (the default; --no-reuse-cell turns it off) - the shots of this cell "
                        + "share owned record facts, asset resolution, decoded textures and parsed meshes; all are dropped "
                        + "when the cell is done");
    }

    /// <summary>The resolver and decoded-pixel cache this cell's shots share, built on first use.
    ///
    /// ONE slot, not two, because the cache captures the resolver in its constructor and resolves
    /// every key through it: a cache leased apart from its resolver could be handed to a shot whose
    /// resolver had already been dropped. Made by one factory and held together, they cannot be
    /// paired wrongly and cannot die at different times.</summary>
    public CellAssets Assets(Func<CellAssets> make)
    {
        ObjectDisposedException.ThrowIf(_done, this);
        return _assets ??= make();
    }

    MeshState? _mesh;

    /// <summary>Try the complete owned result of this cell's record walks. A different key never
    /// receives stale facts: the caller performs a fresh walk and replaces the one guarded slot.</summary>
    internal bool TryFacts(string key, out CellWalkFacts facts)
    {
        ObjectDisposedException.ThrowIf(_done, this);
        if (_facts is not null && StringComparer.Ordinal.Equals(_factsKey, key))
        {
            facts = _facts;
            return true;
        }
        facts = null!;
        return false;
    }

    internal void StoreFacts(string key, CellWalkFacts facts, CellFactsProbe probe)
    {
        ObjectDisposedException.ThrowIf(_done, this);
        CellFactsOwnership.Validate(facts);
        _factsKey = key;
        _facts = facts;
        CellFactsRed.Acquired(this, probe);
    }

    /// <summary>The builder and immutable parsed-mesh outcomes shared by this cell's shots.</summary>
    public MeshState Mesh(Func<MeshState> make)
    {
        ObjectDisposedException.ThrowIf(_done, this);
        return _mesh ??= make();
    }

    /// <summary>The cell is done. The lease drops what it held; nothing here owns an unmanaged
    /// handle today (the resolver, pixel cache and parsed mesh arrays are managed, while GL uploads
    /// remain per shot), so this is the scope ending rather than a cleanup routine. A slice
    /// that adds something disposable disposes it HERE, and nowhere else.</summary>
    public void Dispose()
    {
        _facts = null; _factsKey = null;
        _mesh = null; _assets = null; _done = true;
        CellFactsRed.Disposed(this);
    }
}

public sealed class MeshState
{
    readonly Dictionary<string, MeshOutcome> _outcomes = new(StringComparer.OrdinalIgnoreCase);
    public NifMeshBuilder Builder { get; }
    public MeshState(NifMeshBuilder builder) => Builder = builder;
    internal bool TryGet(string rel, out MeshOutcome outcome) => _outcomes.TryGetValue(rel, out outcome!);
    internal void Store(string rel, MeshOutcome outcome) => _outcomes[rel] = outcome;
}

internal sealed record MeshOutcome(List<NifMeshBuilder.BuiltMesh>? Built, string InputHash,
    Exception? Failure, bool MissingPath);

/// <summary>
/// The asset-side pair one cell's shots share, made together so they cannot be mismatched. Parsed
/// mesh state is the lease's separate managed slot because it owns its builder and outcome table.
///
/// <para><b>The resolver.</b> Its inputs (the game data folder, the archive view, the loose-folder
/// precedence list) are PROCESS-level — a shot cannot change any of them. Sharing it is worth
/// doing because its extraction index is an instance field, so without the lease the second shot of
/// a cell re-extracts from the archives every file the first shot already extracted.</para>
///
/// <para><b>The decoded-pixel cache.</b> It holds BGRA32 pixels, cubemap faces and a
/// "decodes to fully transparent" verdict, each keyed on the RESOLVED DISK PATH rather than the
/// game-relative one, plus an NPC mesh-path cache this renderer never fills (its data source
/// resolves nothing) and a parsed-NIF cache of its own that viewprobe never reads — the scene's
/// library-owned parsed-NIF cache is not the scene's retained mesh table. Nothing in those keys or values is a camera decision:
/// `--cull-above` is applied when the upload's index arrays are built, not by mutating decoded
/// pixels, and the library's contract treats a decoded array as immutable — the texture manager's
/// tint paths clone before they write. Sharing it is worth doing because without the lease the
/// second shot of a cell decodes through Pfim every texture the first shot already decoded.</para>
///
/// <para><b>What it deliberately is not.</b> Neither object owns a GL handle: every texture the
/// renderer uploads belongs to the per-shot <c>GlTextureManager</c>, which deletes its own handles
/// when the shot's context goes. The lease therefore cannot hand a shot a handle from a dead
/// context — the question of the context's own lifetime is a separate one, not settled here.</para>
/// </summary>
/// <param name="Resolver">The asset resolver this cell's shots resolve through.</param>
/// <param name="Cache">The decoded-pixel cache built ON that resolver.</param>
public sealed record CellAssets(GameAssetResolver Resolver, AssetCache Cache);

/// <summary>
/// The lease's three promises — one set of shared objects per cell, a NEW set for the next cell,
/// and nothing at all once the cell is done — over objects built in the test rather than rendered,
/// so a check can hold the lifetime itself. Needs no MO2 instance, no game, no load order and no GL
/// context: the resolver is pointed at a data folder that need not exist and a VFS that is never
/// asked anything, and neither object touches the disk until something resolves an asset.
/// </summary>
public static class CellSceneSelfTest
{
    /// <summary>The library wants somewhere to look for archives. A lifetime test resolves no
    /// asset, so this one answers "not in an archive" and says why if anything ever asks.</summary>
    sealed class NoBsa : IBsaArchiveProvider
    {
        public void EnsureAllArchivesOpened() { }
        public bool TryLocateInBsa(string subpath, out string? containingBsaPath)
        { containingBsaPath = null; return false; }
        public bool TryLocateInScopedBsa(string subpath, string folderPath,
                                         IReadOnlyList<string> modKeyFileNames, out string? containingBsaPath)
        { containingBsaPath = null; return false; }
        public bool TryExtractToDisk(string containingBsaPath, string subpath, string destPath, out string? error)
        { error = "the cell-scene self-test resolves no assets"; return false; }
    }

    /// <summary>A resolver and a cache built on it — the same two lines the render path builds,
    /// against a data folder nothing reads.</summary>
    static CellAssets MakePair()
    {
        var logger = new ConsoleCvLogger();
        var gate = new CharacterViewerLogGate();
        // No extraction-scratch bracket, deliberately: the resolver's constructor only captures the
        // extraction directory as a string, and nothing here extracts, so there is no destination
        // for the redirect to protect.
        var resolver = new GameAssetResolver(
            new HcDataFolder(Path.Combine(Path.GetTempPath(), "viewprobe-cell-scene-selftest")),
            new NoBsa(), gate, logger);
        return new CellAssets(resolver,
            new AssetCache(resolver, logger, gate, new CvSettings()));
    }

    public static int Run()
    {
        int built = 0;
        CellAssets Factory() { built++; return MakePair(); }

        // 1. Within one cell the shots share: the factory runs ONCE and both calls get back the
        //    same two objects. A lease that rebuilt per call would be a flag that does nothing.
        var cell1 = new CellScene("SelfTestCellOne");
        var first = cell1.Assets(Factory);
        var second = cell1.Assets(Factory);
        bool sharedWithinTheCell = built == 1
                                && ReferenceEquals(first, second)
                                && ReferenceEquals(first.Resolver, second.Resolver)
                                && ReferenceEquals(first.Cache, second.Cache);

        // 2. The next cell shares NOTHING with the last: a second lease builds its own pair. This
        //    is the hazard the strategy names — a table filled for cell 1 answering in cell 2 —
        //    and it is a scope, so there is nothing to carry over.
        int builtAfterCell1 = built;
        var cell2 = new CellScene("SelfTestCellTwo");
        var third = cell2.Assets(Factory);
        bool nextCellSharesNothing = built == builtAfterCell1 + 1
                                  && !ReferenceEquals(third, first)
                                  && !ReferenceEquals(third.Resolver, first.Resolver)
                                  && !ReferenceEquals(third.Cache, first.Cache);

        // 3. Once the cell is done the lease is done: a disposed lease REFUSES rather than building
        //    a fresh pair. A lease that answered here would be a process-lifetime cache wearing a
        //    scope's clothes — dropping the field is not the same promise as ending the scope.
        cell1.Dispose();
        cell2.Dispose();
        int builtBeforeRefusal = built;
        string? refusal = null;
        try { cell2.Assets(Factory); }
        catch (ObjectDisposedException e) { refusal = e.GetType().Name; }
        bool nothingAfterTheCell = refusal is not null && built == builtBeforeRefusal;

        bool ownershipBoundaryRefusesGetterShape = CellFactsOwnership.SelfTest();
        bool changedRealInputAcrossOwnersRefuses = PreparedProcessIdentity.ChangedFileAcrossCellOwnersSelfTest();

        var factsLease = new CellScene("FactsSlot");
        var factsA = CellWalkFacts.SelfTestFixture("A");
        var probe = new CellFactsProbe("A", Array.Empty<KeyValuePair<Mutagen.Bethesda.Plugins.FormKey, Mutagen.Bethesda.Plugins.FormKey>>(),
            Array.Empty<string>(), Array.Empty<GridCellFact>(), Array.Empty<WaterTruthFact>(), null,
            float.MaxValue, "", Array.Empty<KeyValuePair<Mutagen.Bethesda.Plugins.FormKey, string>>(),
            Array.Empty<KeyValuePair<Mutagen.Bethesda.Plugins.FormKey, CellBoundsFact>>(), new string('0', 64));
        factsLease.StoreFacts("A", factsA, probe);
        bool factsKeyHit = factsLease.TryFacts("A", out var storedA) && ReferenceEquals(storedA, factsA);
        bool changedSelectionMisses = !factsLease.TryFacts("B", out _);
        var factsB = CellWalkFacts.SelfTestFixture("B");
        factsLease.StoreFacts("B", factsB, probe with { Cell = "B" });
        bool replacementOwned = factsLease.TryFacts("B", out var storedB)
                             && ReferenceEquals(storedB, factsB) && !ReferenceEquals(storedB, storedA);
        factsLease.Dispose();
        bool factsDisposedRefuses = false;
        try { factsLease.TryFacts("B", out _); }
        catch (ObjectDisposedException) { factsDisposedRefuses = true; }

        var verdicts = new Dictionary<string, bool>
        {
            ["sharedWithinTheCell"] = sharedWithinTheCell,
            ["nextCellSharesNothing"] = nextCellSharesNothing,
            ["nothingAfterTheCell"] = nothingAfterTheCell,
            ["getterAndCallbackContaminationRefused"] = ownershipBoundaryRefusesGetterShape,
            ["equalFactsKeyHits"] = factsKeyHit,
            ["changedSelectionKeyMissesThenReplaces"] = changedSelectionMisses && replacementOwned,
            ["disposedFactsSlotRefuses"] = factsDisposedRefuses,
            ["changedRealInputAcrossCellOwnersRefuses"] = changedRealInputAcrossOwnersRefuses,
        };
        var failed = verdicts.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            notMet = failed,                       // what is NOT met, first
            verdicts,
            pairsBuilt = built,
            refusalAfterDispose = refusal,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return failed.Length == 0 ? 0 : 1;
    }
}
