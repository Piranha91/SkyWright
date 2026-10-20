using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// TERRAIN SUPPORT — the pairing (Research/TERRAIN-SUPPORT-DESIGN-2026-10-18.md §6). STUB: the known answers are written first.

/// <summary>What became of one exterior reference in the terrain pass.</summary>
public enum TerrainStatus
{
    NotObject, DeletedAtLoad, DeletedWithPlacement, DeletedInOwnerView, EnableUnresolved, DisabledAtLoad, Marker, NoBody,
    DuplicateGrid, TerrainAgrees, NoAuthoredGround, NotRestingAsAuthored, AuthoredCannotTell, StillResting, Row, Refused, NotBuilt,
}

/// <summary>One footprint cell: who the owner saw there, who won, how far the two terrains differ under the body, and the cell's
/// sight and population.</summary>
public sealed record TerrainCellReading(int GX, int GY, int AuthoredPlugin, int WinnerPlugin, string AuthoredKind, string WinnerKind,
                                        float MaxDelta, bool Changed, TerrainSight Sight, TerrainPopulation Population);

/// <summary>One reference judged. <c>Gap</c>/<c>Burial</c> are the folded winning readings; the per-diagonal clearances ride along.</summary>
public sealed record TerrainRow(FormKey Key, FormKey Base, FormKey Worldspace, Snap Pose, int StepPlugin, int Owner, TerrainStatus Status, string Why,
                                char BodySrc = '-', DependentMotion Motion = DependentMotion.Unknown,
                                TerrainAuthored Authored = TerrainAuthored.NotResting, TerrainVerdict Verdict = TerrainVerdict.Refused,
                                float Gap = float.NaN, float Burial = float.NaN, float Saddle = float.NaN,
                                TerrainSight Sight = TerrainSight.Unchanged, TerrainPopulation Population = TerrainPopulation.Mod, int Winner = -1,
                                IReadOnlyList<TerrainCellReading>? Cells = null, FormKey? Support = null,
                                float AuthoredD0 = float.NaN, float AuthoredD1 = float.NaN, float WinningD0 = float.NaN, float WinningD1 = float.NaN,
                                float BurialD0 = float.NaN, float BurialD1 = float.NaN, float CoreGap = float.NaN, float? Landing = null);

public sealed class TerrainContactIndex
{
    readonly ContactIndex _cidx;
    readonly CellBoundaryIndex _land;
    readonly IReadOnlyList<HashSet<int>> _closure;

    public float Epsilon { get; init; } = 4f;
    public float FloatThreshold { get; init; } = 8f;
    public float BurialThreshold { get; init; } = 8f;
    public float Reach { get; init; } = 256f;
    public Dictionary<string, long> Counts { get; } = new();

    public TerrainContactIndex(ContactIndex cidx, CellBoundaryIndex land, IReadOnlyList<HashSet<int>> closure)
    { _cidx = cidx; _land = land; _closure = closure; }

    /// <summary>The placement owner: the plugin that set the position the game loads.</summary>
    public static int PlacementOwner(int stepPlugin, IReadOnlyList<int>? touchers, IReadOnlyList<HashSet<int>> closure) => stepPlugin;

    /// <summary>Every exterior reference judged (rows of every status when <paramref name="keepAll"/>; else Row and Refused only).</summary>
    public List<TerrainRow> Judge(bool keepAll) => new();

    /// <summary>The admission rule (show only inter-mod clashes): a Fixed Blind Mod-population row that floats or is buried.</summary>
    public static bool Emitted(TerrainRow r) => false;

    /// <summary>One reference, every status.</summary>
    public TerrainRow Explain(FormKey key) => new(key, FormKey.Null, FormKey.Null, default, -1, -1, TerrainStatus.NotBuilt, "not built");
}
