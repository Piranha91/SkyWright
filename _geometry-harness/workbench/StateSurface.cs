using Mutagen.Bethesda.Plugins;
using ViewProbe;

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
    /// <summary>The Work Folder decisions and sessions are kept in; null where none is set.</summary>
    string? WorkFolder => null;
    /// <summary>The instance and profile the scene and the houseCARL server are bound to.</summary>
    (string Instance, string Profile)? Binding => null;
    /// <summary>The open bundle's plan id and arm.</summary>
    string? PlanId => Open?.PlanId;
    string? Arm => Open?.Arm;
    /// <summary>The detector row's state as the bundle measured it: physical state, summary, the probe build.</summary>
    RowState RowStateOf(RankedRow r) => new(r.Row.Physical?.ToString(), r.Row.Summary, "");
    /// <summary>Why the last session save failed, said in the window; null when it did not.</summary>
    string? SaveRefusal { get => null; set { } }
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
    /// <summary>The view-only state; null where the scene has none.</summary>
    ViewState? View => null;
    /// <summary>The scene's pick shapes (drawn references and terrain) at their current placements.</summary>
    IReadOnlyList<PickShape> Shapes => Array.Empty<PickShape>();
    /// <summary>The drawn references whose winning record a plugin of this mod folder provides, in the arm's view, or why not.</summary>
    Task<(IReadOnlyList<FormKey> Refs, string? Refusal)> RefsOfModAsync(string modFolder, string? plugin) =>
        Task.FromResult<(IReadOnlyList<FormKey>, string?)>((Array.Empty<FormKey>(), "this scene cannot read which mod provides a reference."));
    /// <summary>The detector row and the objects a decision on this row is keyed on, read now.</summary>
    Task<(RowState Row, IReadOnlyList<ObjectState> Objects, string ServerBuild)> DecisionInputsAsync(RankedRow row) =>
        throw new InvalidOperationException("this scene has no houseCARL server to read the row's objects from.");
    /// <summary>What the screen-centre ray hits first (a drawn, not hidden, reference or the terrain); null for nothing.</summary>
    PickHit? PickCentre() => null;
    /// <summary>Undo a written edit by removing its override from its patch; one sentence saying what was done.</summary>
    Task<string> RevertWrittenAsync(FormKey key) => throw new InvalidOperationException("this scene has no houseCARL server to remove a written edit with.");
    /// <summary>The folder of a patch this session wrote, by its plugin name; null when not known.</summary>
    string? PatchFolderOf(string patch) => null;
    /// <summary>The camera as [x, y, z, yaw, pitch]; null where there is none.</summary>
    float[]? Camera { get => null; set { } }
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
    public string? WorkFolder => Shell.Launcher.WorkFolder
        ?? (Environment.GetEnvironmentVariable("WB_WORK") is { Length: > 0 } w ? w : Environment.GetEnvironmentVariable("WB_OUT") is { Length: > 0 } o ? o : null);
    public (string Instance, string Profile)? Binding => WorkbenchHost.TargetFromEnv() is { } t ? (t.Instance, t.Profile) : null;
    public string? SaveRefusal { get; set; }

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
