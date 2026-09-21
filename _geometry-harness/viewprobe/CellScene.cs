using CharacterViewer.Rendering;

namespace ViewProbe;

/// <summary>
/// What the shots of ONE CELL may share — an EXPERIMENTAL, opt-in lease behind <c>--reuse-cell</c>.
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
/// lifetime to live in.
///
/// WHAT MAY NOT GO IN HERE, however cheap it looks:
///  - anything the CAMERA decides. `--cull-above` is baked into the uploaded index arrays (a
///    triangle wholly above the cut is dropped before upload), so uploaded meshes belong to the
///    shot even though the parsed mesh does not;
///  - the two build-side camera dependencies the invalidation map names: the route's ground z,
///    which falls back to the eye's height, and `--npc-puck auto`, which reads the camera pitch;
///  - anything whose inputs a shot may change. A shot of a worklist may override the navmesh mode,
///    the cut height, the planes layer, the occlusion mode and the canvas aspect.
///
/// The default is unchanged: with no flag the lease is null and every shot builds its own, exactly
/// as before. The flag is experimental and the suite (`tools/rendertwin.py`) is what says whether a
/// slice may stay: it renders a worklist as one process and each shot again alone, and compares the
/// pixels, the report, the console and the census.
/// </summary>
public sealed class CellScene : IDisposable
{
    /// <summary>The cell this lease was constructed for — it exists for exactly that long.</summary>
    public string Cell { get; }

    GameAssetResolver? _resolver;

    public CellScene(string cell)
    {
        Cell = cell;
        // ONE line per lease, not per shot: a per-shot line would differ between a batched shot and
        // the same shot rendered alone, and the equivalence suite compares those two consoles.
        Console.WriteLine("   cell scene: ON (--reuse-cell, experimental) - the shots of this cell "
                        + "share the asset resolver; it is dropped when the cell is done");
    }

    /// <summary>The asset resolver for this cell's shots, built on first use.
    ///
    /// Why it is safe to share: its inputs (the game data folder, the archive view, the loose-folder
    /// precedence list) are PROCESS-level — a shot cannot change any of them. Why it is worth
    /// sharing: the resolver's extraction index is an instance field, so today the second shot of a
    /// cell re-extracts from the archives every file the first shot already extracted.</summary>
    public GameAssetResolver Resolver(Func<GameAssetResolver> make) => _resolver ??= make();

    /// <summary>The cell is done. The lease drops what it held; nothing here owns an unmanaged
    /// handle today (the resolver holds none, and the per-shot path never disposed it either), so
    /// this is the scope ending rather than a cleanup routine. A slice that adds something
    /// disposable disposes it HERE, and nowhere else.</summary>
    public void Dispose() => _resolver = null;
}
