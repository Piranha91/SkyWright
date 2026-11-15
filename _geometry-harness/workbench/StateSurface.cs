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

    public string OpenConflict(RankedRow row)
    {
        Shell.OpenRow(row);
        return Shell.SwitchClock is not null
            ? $"conflict {row.Rank} is in cell {row.Row.Cell}, which is not loaded: the window is rebuilding the scene for it (it does not answer until the scene is built); read the state again in a minute."
            : $"opened conflict {row.Rank} ({row.Row.Kind}, {row.Row.A} / {row.Row.B}): " + (Shell.LastLanding ?? Shell.BannerText);
    }
}
