using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

/// <summary>One candidate position: its number, the horizontal move from the reference's load-order place, and the rule
/// that made it, in words.</summary>
internal sealed record Candidate(int N, Vector3 Move, string Rule);

/// <summary>Item 8's candidates, generated and never judged: from the pair's two boxes, the smallest move along +x, -x, +y
/// and -y that separates them, at 1x and 1.5x that distance, nearest first.</summary>
internal static class Candidates
{
    internal const int Default = 3, Max = 6;

    internal static IReadOnlyList<Candidate> Generate(Aabb subject, Aabb other, int max = Default) => Array.Empty<Candidate>();
}

/// <summary>A candidate's word and sentence, and the rows it names.</summary>
internal sealed record TrialVerdict(string Word, string Sentence, IReadOnlyList<string> Rows);

/// <summary>Item 8's words over the frozen re-check rule (F11): "clear" only when the open row resolved, every other row
/// naming the moved reference resolved, no carried predicate of it still holds and nothing was created.</summary>
internal static class ClearRule
{
    internal const string ClearLimit = "clear of every conflict the re-check measures between mods; the moved object is not measured against scenery no mod is responsible for";

    internal static TrialVerdict Judge(RowVerdict open, IReadOnlyList<RowVerdict> othersOfRef, IReadOnlyList<RowVerdict> created,
                                       IReadOnlyList<string> stillHolding) => new("", "", Array.Empty<string>());

    /// <summary>Every unpatched row naming the moved reference (either side, any case) other than the open row, with its
    /// frozen word from the two emits.</summary>
    internal static IReadOnlyList<RowVerdict> OthersOfRef(string unpatchedEmit, string patchedEmit, string openRowId, string refKey) => Array.Empty<RowVerdict>();

    /// <summary>The carried predicates of the patched emit that still hold for the moved reference, in words.</summary>
    internal static IReadOnlyList<string> StillHolding(string patchedEmitPath, string refKey) => Array.Empty<string>();
}

/// <summary>A candidate as the placements part shows it.</summary>
internal sealed record TrialPlacement(int N, RefPlacementEdit.Pose Pose, string Move, string Drop, IReadOnlyList<string> Contacts,
                                      TrialVerdict? Verdict, double? Seconds, IReadOnlyList<string> Checked, IReadOnlyList<string> NotChecked,
                                      string? PatchFolder);

/// <summary>What one trial's re-check gives the lane: the result, the other rows naming the moved reference and their words,
/// the carried predicates still holding for it, and the population line.</summary>
internal sealed record TrialRecheck(RecheckResult Result, IReadOnlyList<RowVerdict> OthersOfRef, IReadOnlyList<string> StillHolding, string Population);

/// <summary>Item 8's trial lane: each candidate written as its own patch under the Work Folder's trials folder from a made-up
/// net edit (load-order pose to candidate pose), never through the edit session, and judged by the re-check, one at a time.</summary>
internal sealed class TrialLane
{
    internal delegate Task<string> WriteTrial(PendingEdit edit, string patch, string outPath);
    internal delegate Task<TrialRecheck> RecheckTrial(string patch, string patchFolder, string workDir);

    /// <summary>The measured cost of one candidate (both runs), in seconds: the low and high of the Gate A probe.</summary>
    internal const double SecondsLow = 67, SecondsHigh = 76;

    internal TrialLane(string? workFolder, WriteTrial write, RecheckTrial recheck,
                       Func<RefPlacementEdit.Pose, (RefPlacementEdit.Pose Pose, string Drop)> settle,
                       Func<RefPlacementEdit.Pose, IReadOnlyList<string>> contactsAt) { }

    internal Task? Running => null;
    internal IReadOnlyList<TrialPlacement> Placements => Array.Empty<TrialPlacement>();
    internal string? Cell => null;

    /// <summary>Start the trials of <paramref name="key"/> from its load-order pose; returns the cost sentence at once.</summary>
    internal string Start(FormKey key, string? cell, RefPlacementEdit.Pose loadOrder, IReadOnlyList<Candidate> candidates, string probe,
                          int otherStaged, bool writtenSame, float grid = 0f) => "";

    /// <summary>The pose to stage for candidate <paramref name="n"/>, or why not.</summary>
    internal (RefPlacementEdit.Pose? Pose, string Said) Choose(int n, string? cell, RefPlacementEdit.Pose current) => (null, "");

    internal static string CostSentence(int candidates) => "";

    /// <summary>What a re-check of this probe does not check, said with every candidate.</summary>
    internal static IReadOnlyList<string> NotCheckedFor(string probe, string population) => Array.Empty<string>();
}
