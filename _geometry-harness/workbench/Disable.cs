using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace Workbench;

/// <summary>What the records say about one reference a disable is asked for (Gate B; not built yet).</summary>
internal sealed record DisableFacts(string Key);

/// <summary>Whether a reference may be disabled, and the one sentence when not (Gate B; not built yet).</summary>
internal static class DisableRule
{
    internal static Task<DisableFacts> ReadAsync(IContextSource src, FormKey key) => Task.FromResult(new DisableFacts(key.ToString()));
    internal static string? Refusal(DisableFacts f) => null;
    internal static string? Note(DisableFacts f) => null;
}

/// <summary>The written disable read back by Mutagen, not by the writer (Gate B; not built yet).</summary>
internal static class DisableReadBack
{
    internal static void Check(IReadOnlyList<string> orderPaths, string patchPath, FormKey key) { }
    internal static IReadOnlyList<string> Differences(PlacedObject winner, PlacedObject written) => Array.Empty<string>();
}

/// <summary>What a re-check says after a disable (Gate B; not built yet).</summary>
internal sealed record DisableRecheckResult(RowVerdict Row, IReadOnlyList<RowVerdict> Others, IReadOnlyList<RowVerdict> New);

internal static class DisableRecheck
{
    internal static DisableRecheckResult Of(DifferentialRows.Credited overlap, DifferentialRows.Credited? contact, string key, string openId) =>
        new(new RowVerdict(openId, "not in either run", "absent", ""), Array.Empty<RowVerdict>(), Array.Empty<RowVerdict>());
}
