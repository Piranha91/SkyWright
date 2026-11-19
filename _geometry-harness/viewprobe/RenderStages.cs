using HousecarlCore.Progress;

namespace ViewProbe;

/// <summary>
/// What part of a render is running, and how far through it is.
///
/// A render used to file itself under one stage — `find-cell`, opened while the load order was
/// walked for the target cell and then never replaced — so a seventeen-second render reported
/// fifteen seconds of "walking the order for the target cell" at one plugin of five, and a
/// multi-shot run repeated that same stage once per shot. The run record's `stageSource` said
/// `single-stage` while the coverage manifest claimed `counted`, and nothing checked the claim.
///
/// The cuts here are the ones the cost model already measures, so a saving can be read out of
/// history rather than re-measured by hand:
///
///   find-cell     walking the order until the cell is found — per shot, because the tool
///                 re-walks per shot
///   record-walk   the winning state: every plugin, for the refs and navmeshes that win
///   scene-build   resolving and building the meshes and textures of every reference, and drawing
///   report        the occlusion report's own per-shape visibility work, when one is asked for
///   encode        encoding the frame and writing it
///
/// Weights are left at the default. An unmeasured weight is a claim about the split that this
/// build cannot make yet; the estimator learns the shape from recorded history instead.
///
/// Every call is a no-op when no run is open (a smoke entry point, or a host with the log
/// disabled), so nothing here can fail a render.
/// </summary>
public static class RenderStages
{
    public const string FindCell = "find-cell";
    public const string RecordWalk = "record-walk";
    public const string SceneBuild = "scene-build";
    public const string Report = "report";
    public const string Encode = "encode";

    /// <summary>Enter a stage. <paramref name="total"/> null is an explicitly UNKNOWN total, which
    /// the contract allows and a made-up total does not.</summary>
    public static void Open(string id, string title, long? total, string unit)
    { RunLog.Current?.Stage(id, title, total, unit); Tick?.Invoke(); }

    /// <summary>A host's hook, called on the building thread at each stage step and inside the long walks: the conflict
    /// review workbench answers its window here, and may throw <see cref="BuildAbandoned"/>. Null outside a host.</summary>
    public static Action? Tick;

    /// <summary>One more unit of the current stage's work.</summary>
    public static void Advance(long delta = 1) { RunLog.Current?.Advance(delta); Tick?.Invoke(); }

    /// <summary>Set the measured processing of the current stage.</summary>
    public static void Progress(long processed) => RunLog.Current?.Progress(processed);

    /// <summary>The total was right at the start and is wrong now, for a reason worth recording —
    /// the cell was found in the second plugin of five, so the other three were never work. §5
    /// allows a revision with a stated reason; it does not allow finishing at 1 of 5 in silence.
    /// </summary>
    public static void Revise(long? total, string reason)
        => RunLog.Current?.ReviseTotal(total, reason);

    /// <summary>A workload feature, for the estimator. Numbers only — an identifier is a resource.</summary>
    public static void Feature(string name, double value, string unit)
        => RunLog.Current?.Feature(name, value, unit);

    /// <summary>An opaque identifier of what this run rendered, recorded only when the run had
    /// already resolved one. The contract permits local opaque identifiers for inputs and forbids
    /// carrying the inputs themselves; a configuration fingerprint is a hash and names no plugin.
    /// </summary>
    public static void Configuration(string? fingerprint)
    {
        if (!string.IsNullOrEmpty(fingerprint)) RunLog.Current?.Resource("configuration", fingerprint);
    }
}
