using Mutagen.Bethesda.Plugins;

namespace SkyWright.Engine;

// ======================================================================
//  WorldRead — the SHARED, CACHED WORLD READ for given cells (shared world read, unit 1).
//
//  One prepared file per plugin (WorldReadFile), keyed by the plugin file's content hash, the file's schema and the
//  Engine's module version id, holds a directory of every placed record the plugin holds, those records filed by
//  cell, the cells it holds and the facts of its modeled base records. A read of some cells replays the wanted cell
//  sections in load order and asks the directories which plugin's record of each reference wins: the last plugin in
//  the order that holds a record of it at all, with or without placement data. Record headers carry no position, so
//  every "where" comes from placement data.
// ======================================================================

/// <summary>A problem the reader cannot work around: one plain sentence saying what went wrong and what to try.</summary>
public sealed class WorldReadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One cell as the last plugin in the order that holds its record states it.</summary>
/// <param name="Worldspace">Null for an interior.</param>
/// <param name="Grid">Null for an interior and for a worldspace's persistent cell.</param>
/// <param name="IsPersistentCell">The worldspace's persistent cell (its top cell), which holds persistent references
/// wherever they stand.</param>
public sealed record WorldCell(FormKey Cell, string? EditorId, bool Interior, FormKey? Worldspace, (int X, int Y)? Grid, bool IsPersistentCell);

/// <summary>The winning record of one reference.</summary>
/// <param name="Ref">Everything the record's placement says; null when the winning record carries no placement data
/// (a deletion, nearly always).</param>
/// <param name="Plugin">The load-order index of the plugin whose record wins.</param>
/// <param name="Cell">The cell the winning record is filed under.</param>
/// <param name="Persistent">The winning record is in its cell's persistent list.</param>
/// <param name="Deleted">The winning record is flagged deleted.</param>
public sealed record WorldRef(FormKey Reference, PlacedRef? Ref, int Plugin, string PluginName, FormKey Cell, bool Persistent, bool Deleted)
{
    public bool HasPlacement => Ref is not null;
}

/// <summary>A reference an earlier plugin placed in the requested area whose winning record stands elsewhere.</summary>
/// <param name="FromPlugin">The last plugin whose record of it is in the requested area.</param>
/// <param name="FromCell">The cell that plugin files it under.</param>
/// <param name="Winner">The winning record, and where it is filed.</param>
public sealed record MovedOutRef(FormKey Reference, int FromPlugin, string FromPluginName, FormKey FromCell, WorldRef Winner);

/// <summary>What stands in the requested cells.</summary>
/// <param name="Cells">The requested cells that some plugin holds.</param>
/// <param name="Winners">The references standing in the area: winning, not deleted, with placement data.</param>
/// <param name="MovedOut">References placed in the area by an earlier plugin whose winning record stands elsewhere.</param>
/// <param name="Deleted">References seen in the area whose winning record is deleted, with or without placement
/// data, wherever that record is filed.</param>
/// <param name="Unplaced">References seen in the area whose winning record carries no placement data and is not
/// flagged deleted: it has no position the reader could give.</param>
public sealed record WorldAnswer(
    IReadOnlyList<WorldCell> Cells,
    IReadOnlyList<WorldRef> Winners,
    IReadOnlyList<MovedOutRef> MovedOut,
    IReadOnlyList<WorldRef> Deleted,
    IReadOnlyList<WorldRef> Unplaced);

/// <summary>The facts of one modeled base record, as the winning plugin's record gives them.</summary>
/// <param name="TypeName">Mutagen's record type name ("Static", "Light", "Armor", ...).</param>
/// <param name="Model">The model it is drawn with in the world (<see cref="BaseModel.Of"/>).</param>
/// <param name="FemaleModel">An armour's female ground model, else its male one (<see cref="BaseModel.FemaleOf"/>); null
/// for any other record.</param>
/// <param name="Obnd">OBND extents, |second − first| per axis; null when the record has none.</param>
/// <param name="IsMarker">The Is-Marker header flag (0x00800000).</param>
/// <param name="LightCanBeCarried">A light flagged Can Be Carried.</param>
public sealed record BaseFacts(FormKey Key, string TypeName, string? EditorId, string? Model, string? FemaleModel, Vec3? Obnd,
                               bool IsMarker, bool LightCanBeCarried, int Plugin, string PluginName);

/// <summary>Where <see cref="WorldRead.Open"/> spent its time, in seconds.</summary>
public sealed record WorldReadTimings(double HashSeconds, double PrepareSeconds, double LoadSeconds);

/// <summary>The shared world read over one load order. Thread-safe for reads once open.</summary>
public sealed class WorldRead
{
    /// <summary>The prepared file's layout version. A change to what the file holds bumps it.</summary>
    public const int Schema = 1;

    /// <summary>The Engine build the prepared files are made by: any change to the Engine's code prepares every plugin
    /// once more, so a reader never reads a file an older reader wrote.</summary>
    public static Guid EngineVersion => typeof(WorldRead).Module.ModuleVersionId;

    /// <summary>Plugins whose prepared file was made by this <see cref="Open"/>.</summary>
    public int Prepared { get; private set; }
    /// <summary>Plugins whose prepared file was already present and read.</summary>
    public int Reused { get; private set; }
    /// <summary>Schema, Engine version and the ordered plugin names with their content hashes, for receipts.</summary>
    public string Identity { get; private set; } = "";
    public WorldReadTimings Timings { get; private set; } = new(0, 0, 0);
    /// <summary>The plugins' file names, in load order.</summary>
    public IReadOnlyList<string> PluginNames { get; private set; } = Array.Empty<string>();

    WorldRead() { }

    /// <summary>Hash each plugin, prepare the files that are missing or do not match, and load their directories.</summary>
    public static WorldRead Open(IReadOnlyList<string> orderedPluginPaths, string cacheFolder) => new();

    /// <summary>Every cell some plugin holds, as the last plugin holding it states it.</summary>
    public IReadOnlyList<WorldCell> AllCells => Array.Empty<WorldCell>();

    /// <summary>One cell by its key, whole: an interior, a grid cell or a worldspace's persistent cell.</summary>
    public WorldAnswer Cell(FormKey cell) => Empty;

    /// <summary>The grid cells of <paramref name="worldspace"/> within <paramref name="ring"/> cells of (x, y), and
    /// with <paramref name="includePersistent"/> the worldspace's persistent references whose position lies in them.</summary>
    public WorldAnswer Exterior(FormKey worldspace, int x, int y, int ring, bool includePersistent) => Empty;

    /// <summary>The winning record of a reference wherever it is filed; null when no plugin holds one.</summary>
    public WorldRef? Lookup(FormKey reference) => null;

    /// <summary>The winning facts of a modeled base record wherever it is defined; null when no plugin holds one.</summary>
    public BaseFacts? Base(FormKey key) => null;

    static readonly WorldAnswer Empty = new(Array.Empty<WorldCell>(), Array.Empty<WorldRef>(), Array.Empty<MovedOutRef>(),
                                            Array.Empty<WorldRef>(), Array.Empty<WorldRef>());
}
