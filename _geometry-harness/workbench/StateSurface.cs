using Mutagen.Bethesda.Plugins;

namespace Workbench;

/// <summary>What the MCP state tools read and drive: the app shell (the bundle, its ranked rows, the open conflict) and
/// the scene shown now. Every member is called on the UI thread (<see cref="StateHub"/> dispatches there). The live
/// implementation reads <see cref="Shell"/> and the current <see cref="WorkbenchHost"/>; there is no copy of the state.</summary>
internal interface IWorkbenchSurface
{
    object? Bundle { get; }
    IReadOnlyList<RankedRow> Listed { get; }
    int Total { get; }
    RankedRow? OpenRow { get; }
    string Banner { get; }
    /// <summary>The cell a cell switch is loading, or null. Also read off the UI thread, while that thread is busy.</summary>
    string? Loading { get; }
    /// <summary>The scene shown now; null while none is (a cell switch between scenes).</summary>
    IWorkbenchScene? Scene { get; }
    /// <summary>What a click on the Conflicts tab's row does; returns what happened, in one sentence.</summary>
    string OpenConflict(RankedRow row);
    /// <summary>The bundle open, and the severity table, the Findings tab ranks from; null when none is open.</summary>
    BundleConflicts? Open => null;
    SeverityTable? Severity => null;
    /// <summary>The Findings tab, ranked.</summary>
    IReadOnlyList<RankedFinding> Findings => Open is { } b ? FindingRanking.Rank(b.Findings, b.Targets, Severity) : Array.Empty<RankedFinding>();
    /// <summary>What a click on a clickable Findings row does: the Records tree's path to its reference and cell.</summary>
    string OpenFinding(RankedFinding row) => $"finding {row.Rank} names {row.Row.RefKey} in cell {row.Row.Cell}, but no window is shown to open it in.";
}

/// <summary>The scene's side: the edit session and the members the bar and the script call.</summary>
internal interface IWorkbenchScene
{
    EditSession Session { get; }
    string? Cell { get; }
    string Status { get; }
    string? CellOf(FormKey key);
    void Select(FormKey? key);
    void AfterStage();
    /// <summary>Dry-run or write; a real write starts the open row's re-check without waiting for it.</summary>
    Task<string?> CommitAsync(bool dryRun, string patch);
    string? CommitRefusal { get; }
    Task<string?> StartRecheck(string patch);
    Task? RecheckRunning { get; }
    RecheckResult? LastRecheck { get; }
    string? RecheckRefusal { get; }
    bool Rechecking { get; }
}

/// <summary>The running app: <see cref="Shell"/> and its current scene.</summary>
internal sealed class LiveSurface : IWorkbenchSurface
{
    public object? Bundle => Shell.Bundle is { } b ? new
    {
        dir = b.Dir, planId = b.PlanId, arm = b.Arm, targets = b.Targets, verify = b.VerifySummary, notes = b.Notes,
        severityTable = Shell.Severity?.Source,
    } : null;
    public IReadOnlyList<RankedRow> Listed => Shell.Visible;
    public int Total => Shell.Ranked.Count;
    public RankedRow? OpenRow => Shell.CurrentRow;
    public string Banner => Shell.BannerText;
    public string? Loading => Shell.SwitchClock is not null ? Shell.SwitchCell : null;
    public IWorkbenchScene? Scene => Shell.Current;
    public BundleConflicts? Open => Shell.Bundle;
    public SeverityTable? Severity => Shell.Severity;
    public IReadOnlyList<RankedFinding> Findings => Shell.Findings;

    public string OpenFinding(RankedFinding row)
    {
        Shell.CurrentRow = null;
        Shell.OpenRef(row.Row.RefKey!, row.Row.Cell);
        return Shell.SwitchClock is not null
            ? $"finding {row.Rank} is in cell {row.Row.Cell}, which is not loaded: the window is rebuilding the scene for it; read the state again in a minute."
            : $"opened finding {row.Rank} ({row.Row.CheckId}) at {row.Row.RefKey}: " + (Shell.LastLanding ?? Shell.BannerText);
    }

    public string OpenConflict(RankedRow row)
    {
        Shell.OpenRow(row);
        return Shell.SwitchClock is not null
            ? $"conflict {row.Rank} is in cell {row.Row.Cell}, which is not loaded: the window is rebuilding the scene for it (it does not answer until the scene is built); read the state again in a minute."
            : $"opened conflict {row.Rank} ({row.Row.Kind}, {row.Row.A} / {row.Row.B}): " + (Shell.LastLanding ?? Shell.BannerText);
    }
}
