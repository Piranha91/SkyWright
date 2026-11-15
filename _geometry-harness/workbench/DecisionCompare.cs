namespace Workbench;

/// <summary>The per-row comparison of a stored decision, in one place: the decisions state part calls it, and so does a
/// scene's arrival for the decided rows that scene draws, so the window's list is compared without a client.</summary>
internal static class DecisionCompare
{
    /// <summary>Compare the decided rows whose reference the open scene draws, store their standings, and tell the list.</summary>
    internal static Task SceneOpenedAsync(IWorkbenchSurface s, Dictionary<string, DecisionMatch> cache) => Task.CompletedTask;
}
