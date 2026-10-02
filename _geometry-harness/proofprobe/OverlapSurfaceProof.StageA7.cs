using HousecarlCore;

/// <summary>G1 build list, Stage A, row 5 (plan `g1/PLAN-7.md` §6; Gate A review Q8: `--surface` only): the live-pose rule
/// a caller gets when it names none is Targeted. Red on the base, whose default is Blanket.</summary>
static partial class OverlapSurfaceProof
{
    public static void RunStageA7(Action<string, bool, string> check)
    {
        // L0: the named default. Rejects the default left at Blanket.
        Case(check, "stageA7 L0: LivePoseDefault.Policy is Targeted", () =>
            (LivePoseDefault.Policy == LivePosePolicy.Targeted, $"default {LivePoseDefault.Policy}"));
        // L1: ModelSurface with no policy on the rig whose manager animates only a shapeless node measures 24 triangles
        // (SC14a's Targeted answer). Rejects the default still blanket (refused: live pose).
        Case(check, "stageA7 L1: no policy named, manager animates a shapeless node — measured, 24 triangles", () =>
        {
            var t = MeshSurface.ModelSurface(Rig("Hinge"), VolumeGeometry.Render);
            return (t.Status == "measured" && t.Soup?.TriangleCount == 24, $"{t.Status} {t.Reason} {t.Soup?.TriangleCount}");
        });
        // L2: Blanket stays selectable and reproduces the old refusal. Rejects the old rule lost.
        Case(check, "stageA7 L2: Blanket named — the same rig refuses (live pose), as before", () =>
        {
            var b = MeshSurface.ModelSurface(Rig("Hinge"), VolumeGeometry.Render, LivePosePolicy.Blanket);
            return (b.Status == "refused" && (b.Reason ?? "").Contains("live pose") && LivePoseDefault.Policy == LivePosePolicy.Targeted, $"{b.Status} {b.Reason}; default {LivePoseDefault.Policy}");
        });
        // L3: with no policy, a shape the manager animates (P's node) still refuses naming the node. Rejects the default
        // measuring a moving shape at its stored pose.
        Case(check, "stageA7 L3: no policy named, manager animates P's node — refused: animated node 'Leaf'", () =>
        {
            var t = MeshSurface.ModelSurface(Rig("Leaf"), VolumeGeometry.Render);
            return (t.Status == "refused" && (t.Reason ?? "").Contains("animated node 'Leaf'") && LivePoseDefault.Policy == LivePosePolicy.Targeted, $"{t.Status} {t.Reason}; default {LivePoseDefault.Policy}");
        });
    }
}
