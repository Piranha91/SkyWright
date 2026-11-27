using System.Numerics;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using ViewProbe;

namespace Workbench;

// Gate C step 2: the entry points the strict tests call, as stubs. The feature comes in step 3.

/// <summary>The kinds a nudge refuses, read from the owner's data file (data/nudge-refusals.json).</summary>
internal sealed record NudgeKinds(IReadOnlyDictionary<string, string> BaseTypes, IReadOnlyDictionary<string, string> Facts,
                                  IReadOnlyDictionary<string, string> NamedBy, IReadOnlySet<string> Carryable, string Source)
{
    internal const string FileName = "nudge-refusals.json";

    internal static NudgeKinds Load(string path) => new(new Dictionary<string, string>(), new Dictionary<string, string>(),
        new Dictionary<string, string>(), new HashSet<string>(), path);
}

/// <summary>What a nudge may move, its cap and its words.</summary>
internal static class NudgeRule
{
    internal const float Cap = 107f;
    internal const string Checked = "clear of the other object and still supported";
    internal const string RoutesNotChecked = "walking routes not checked";

    internal static (string? Refusal, string? Note) Refusal(DisableFacts f, NudgeKinds kinds) => (null, null);

    /// <summary>Gate B's one-fix rule, said for a nudge; null when the object has no staged or written fix.</summary>
    internal static string? OneFix(EditSession s, FormKey key) => null;
}

/// <summary>One candidate move: its number, the horizontal move from the load-order place, the rule that made it.</summary>
internal sealed record NudgeCandidate(int N, Vector2 Move, string Rule);

/// <summary>A candidate after its drop, within the cap.</summary>
internal sealed record NudgeSettled(NudgeCandidate Candidate, RefPlacementEdit.Pose Pose, string Drop);

internal static class NudgeSearch
{
    internal static IReadOnlyList<NudgeCandidate> Candidates(Aabb subject, Aabb other, float cap = NudgeRule.Cap) => Array.Empty<NudgeCandidate>();

    internal static IReadOnlyList<NudgeSettled> Settled(RefPlacementEdit.Pose home, IReadOnlyList<NudgeCandidate> candidates,
        Func<RefPlacementEdit.Pose, (RefPlacementEdit.Pose Pose, string Drop)> settle, float cap = NudgeRule.Cap)
        => candidates.Select(c =>
        {
            var (p, d) = settle(home with { Position = new Vec3(home.Position.X + c.Move.X, home.Position.Y + c.Move.Y, home.Position.Z) });
            return new NudgeSettled(c, p, d);
        }).ToList();
}

/// <summary>What the judge program asked: the subject, the other object and each settled candidate pose.</summary>
internal sealed record NudgeRequest(FormKey Subject, FormKey Other, RefPlacementEdit.Pose Home, IReadOnlyList<(int N, RefPlacementEdit.Pose Pose)> Candidates);

/// <summary>The judge's word on one candidate.</summary>
internal sealed record NudgeJudged(int N, bool Clear, bool OtherHit, IReadOnlyList<string> NewCrossings, IReadOnlyList<string> NewUnknown);

/// <summary>The judge program's answer.</summary>
internal sealed record NudgeJudgeAnswer(IReadOnlyList<NudgeJudged> Candidates, IReadOnlyList<string> HomeCrossings, int Neighbours, int NoGeometry,
                                        double Seconds, string? Refused = null);

/// <summary>The proof's word on a candidate (layer 2).</summary>
internal static class NudgeProofRule
{
    internal static TrialVerdict Verdict(TrialRecheck rc) => new("clear", "clear", Array.Empty<string>());
}

/// <summary>One proof run: the candidate, its word, its sentence and its seconds.</summary>
internal sealed record NudgeProofRun(int N, string Word, string Sentence, double Seconds);

/// <summary>The nudge for one object.</summary>
internal sealed class NudgeLane
{
    internal delegate Task<NudgeJudgeAnswer> Judge(NudgeRequest request, CancellationToken ct);
    internal delegate Task<TrialRecheck> Prove(PendingEdit edit, int n, CancellationToken ct);

    internal const int MaxProofs = 3;

    internal NudgeLane(Func<RefPlacementEdit.Pose, (RefPlacementEdit.Pose Pose, string Drop)> settle, Judge judge, Prove prove,
                       Func<RefPlacementEdit.Pose, string, string> stage) { }

    internal IReadOnlyList<NudgeProofRun> Proofs => Array.Empty<NudgeProofRun>();

    internal Task<string> RunAsync(FormKey key, string? name, RefPlacementEdit.Pose home, Aabb subject, Aabb other, CancellationToken ct)
        => Task.FromResult("");
}
