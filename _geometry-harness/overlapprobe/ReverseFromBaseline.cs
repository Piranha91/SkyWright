using System.Text.Json;
using HarnessShared;

// --reverse-of BASELINE_EMIT (overlap acceleration P1). A reverse run measures the BASELINE order with a candidate's emit as
// --base. Its sweep, provenance and class are the baseline run's own, recomputed: the same order, the same code, the same
// rows. This mode takes those sections from the baseline emit instead, after checking it was made on the same configuration,
// population, state and measurement policy, and re-measures only the --base rows (Predicates), with the code a full reverse
// uses. Any check that fails refuses by name; nothing is carried from an emit this run cannot show is its own baseline.
static class ReverseFromBaseline
{
    static readonly JsonSerializerOptions Canon = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    static string Text<T>(T value) => JsonSerializer.Serialize(value, Canon);

    /// <summary>Null when <paramref name="baseline"/> may stand for this run's own sweep; otherwise the refusal sentence.
    /// <paramref name="ownLines"/> are this run's coverage lines that do not depend on the sweep, provenance or reconciliation
    /// (plugins, candidate refs, population scope): each must equal the baseline's line of the same name.</summary>
    public static string? Refusal(ConflictFile baseline, string baselinePath, string instance, ConfigurationIdentity identity,
                                  string population, string? state, string policy, PairLimits limits, IEnumerable<CoverageLine> ownLines)
    {
        string name = Path.GetFileName(baselinePath);
        if (baseline.Probe != "overlapprobe")
            return $"--reverse-of {name} was written by {baseline.Probe}, not overlapprobe; give overlapprobe's baseline emit, or run the reverse without --reverse-of.";
        if (baseline.BaseEmit is not null || baseline.Predicates is not null)
            return $"--reverse-of {name} was itself run with --base ({baseline.BaseEmit}); give the baseline emit, which has no --base.";
        if (baseline.Configuration is null)
            return $"--reverse-of {name} names no configuration, so it cannot be shown to be this order's baseline; run the reverse without --reverse-of.";
        if (baseline.Configuration.Fingerprint != identity.Fingerprint)
            return $"--reverse-of {name} was measured on configuration {baseline.Configuration.Fingerprint[..12]}, this run on {identity.Fingerprint[..12]}; give the baseline emit of this order, or run the reverse without --reverse-of.";
        if (Text(baseline.Configuration) != Text(identity))
            return $"--reverse-of {name} has this run's fingerprint but a different configuration record (source, limits or unknowns); run the reverse without --reverse-of.";
        if (baseline.Population != population || baseline.State != state)
            return $"--reverse-of {name} judged population {baseline.Population ?? "unknown"} / state {baseline.State ?? "unknown"}, this run {population} / state {state ?? "unknown"}; give the baseline emit of this population and state.";
        if (baseline.MeasurementPolicy != policy)
            return $"--reverse-of {name} was measured under policy '{baseline.MeasurementPolicy ?? "unknown"}', this run under '{policy}'; run the reverse without --reverse-of.";
        if (baseline.PairLimits is null)
            return $"--reverse-of {name} does not record the pair limits it was measured under, so it cannot be shown to be this run's baseline; run the reverse without --reverse-of.";
        if (baseline.PairLimits != limits)
            return $"--reverse-of {name} was measured under pair limits of {baseline.PairLimits.MaxTrianglePairs:N0} triangle pairs and {baseline.PairLimits.PairSeconds:R} s, this run under {limits.MaxTrianglePairs:N0} and {limits.PairSeconds:R} s; give the baseline emit measured under this run's limits.";
        if (baseline.Instance != instance)
            return $"--reverse-of {name} was measured on instance {baseline.Instance}, this run on {instance}; give the baseline emit of this instance.";
        var theirs = (baseline.Coverage ?? Array.Empty<CoverageLine>()).ToDictionary(c => c.Population, StringComparer.Ordinal);
        foreach (var own in ownLines)
        {
            if (!theirs.TryGetValue(own.Population, out var line))
                return $"--reverse-of {name} has no '{own.Population}' coverage line; it is not this run's baseline.";
            if (Text(line) != Text(own))
                return $"--reverse-of {name}'s '{own.Population}' coverage differs from this run's ({Text(line)} against {Text(own)}); it is not this run's baseline.";
        }
        return null;
    }

    /// <summary>Whether the baseline's base-record coverage can be this run's: every status count this run's candidate stage
    /// reached is at most the baseline's (the baseline also counted the bases its provenance stage looked up).</summary>
    public static string? BaseRecordsRefusal(ConflictFile baseline, string baselinePath, long measured, IReadOnlyDictionary<string, long> unknown)
    {
        var line = baseline.Coverage?.FirstOrDefault(c => c.Population == "base records (strict render geometry)");
        string name = Path.GetFileName(baselinePath);
        if (line is null) return $"--reverse-of {name} has no base-record coverage line; it is not this run's baseline.";
        if (measured > line.Judged) return $"--reverse-of {name} counts {line.Judged:N0} measured base records, fewer than this run's candidates reached ({measured:N0}); it is not this run's baseline.";
        foreach (var (k, v) in unknown)
            if (v > (line.Unjudgeable?.GetValueOrDefault(k) ?? 0))
                return $"--reverse-of {name} counts fewer '{k}' base records ({line.Unjudgeable?.GetValueOrDefault(k) ?? 0:N0}) than this run's candidates reached ({v:N0}); it is not this run's baseline.";
        return null;
    }
}
