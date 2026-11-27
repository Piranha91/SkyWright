using HarnessShared;
using Mutagen.Bethesda.Plugins;
using SkyWright.Engine;

/// <summary>A nudge candidate's word from the cheap judge (Gate C layer 1).</summary>
public sealed record NudgeVerdict(int N, bool Clear, bool OtherHit, IReadOnlyList<(FormKey Key, string Who, string Relation)> NewCrossings,
                                  IReadOnlyList<FormKey> NewUnknown, IReadOnlyList<FormKey> Inherited);

/// <summary>overlapprobe --nudge: the cheap judge of a nudge's candidate positions. Gate C step 2 stub.</summary>
public static class NudgeJudge
{
    /// <summary>The subject at each pose against every neighbour whose bounds reach it.</summary>
    public static (NudgeVerdict Home, IReadOnlyList<NudgeVerdict> Candidates) JudgeAll(SurfaceOverlapCandidate subject, FormKey other,
        IReadOnlyList<SurfaceOverlapCandidate> near, IReadOnlyList<(int N, Vec3 Pos)> poses, Func<int, bool> isBaseGame, Func<int, string> pluginName)
        => (new NudgeVerdict(0, false, true, Array.Empty<(FormKey, string, string)>(), Array.Empty<FormKey>(), Array.Empty<FormKey>()),
            poses.Select(p => new NudgeVerdict(p.N, true, false, Array.Empty<(FormKey, string, string)>(), Array.Empty<FormKey>(), Array.Empty<FormKey>())).ToList());
}
