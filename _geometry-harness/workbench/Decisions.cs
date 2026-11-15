namespace Workbench;

/// <summary>One field of a record as the houseCARL server reads it.</summary>
internal sealed record FieldValue(string Path, string Value);

/// <summary>One object a decided row names, as its owners read it when the decision was made: the winning record and its
/// base (fields from the houseCARL server), and the base's model with its winning copy's content hash.</summary>
internal sealed record ObjectState(string FormKey, string Winner, IReadOnlyList<FieldValue> Fields,
                                   string? BaseKey, IReadOnlyList<FieldValue> BaseFields,
                                   string? Model, string? ModelWinner, string? ModelSha256);

/// <summary>The detector's own row when the decision was made: its physical state and summary, and the probe build.</summary>
internal sealed record RowState(string? Physical, string Summary, string ProbeBuild);

/// <summary>A modder's decision on one row of one profile.</summary>
internal sealed record Decision(string Instance, string Profile, string CheckId, string RowKey, string Verdict, string Reason,
                                DateTimeOffset When, RowState Row, IReadOnlyList<ObjectState> Objects, string ServerBuild);

internal enum Standing { Undecided, Applies, Reconsider }

internal sealed record DecisionMatch(Standing Standing, string Sentence);

/// <summary>Real problem / intentional / unsure: what a stored decision means for a row in this run.</summary>
internal static class Decisions
{
    internal static readonly string[] Verdicts = { "real problem", "intentional", "unsure" };

    /// <summary>Fields a re-save changes with nothing a player meets; a change in only these keeps the decision.</summary>
    internal static readonly string[] Bookkeeping = Array.Empty<string>();

    /// <summary>A Conflicts row's key: the detector's row identity.</summary>
    internal static string RowKey(ConflictRow r) => r.Identity;

    /// <summary>A Findings row's key.</summary>
    internal static string FindingKey(FindingRow f) => "";

    /// <summary>Does the stored decision still apply to this row as read now?</summary>
    internal static DecisionMatch Compare(Decision stored, RowState row, IReadOnlyList<ObjectState> now, string serverBuild)
        => new(Standing.Undecided, "");

    /// <summary>The list order with decisions: reconsider, undecided, unsure, real problem, then intentional.</summary>
    internal static IReadOnlyList<T> Order<T>(IReadOnlyList<T> rows, Func<T, (Standing Standing, string? Verdict)> of) => rows;
}

/// <summary>The decisions file under the Work Folder.</summary>
internal sealed class DecisionStore
{
    internal DecisionStore(string path) { }
    internal void Put(Decision d) { }
    internal Decision? Find(string instance, string profile, string rowKey) => null;
    internal IReadOnlyList<Decision> All => Array.Empty<Decision>();
}
