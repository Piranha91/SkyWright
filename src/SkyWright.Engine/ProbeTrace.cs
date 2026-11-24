using Mutagen.Bethesda.Plugins;

using HousecarlCore;

namespace SkyWright.Engine;

// PROBE TRACE — why a probe did or did not flag a given object (patch-miss packet, 2026-09-28).
//
// A probe that records only problems cannot tell "checked and fine" from "never looked at". The trace is an opt-in sink
// the probe calls at every exit of its population, filter and judgement steps, so each requested object carries, per
// role it can play, the step it left at and why. It never changes a verdict: with no sink attached the probe runs as
// before, and with one attached its output is byte-identical (tested). Schema: housecarl.probe-trace/1.

/// <summary>The outcome vocabulary. Exactly one population outcome per (object, role); a judged role may add pair events.</summary>
public static class TraceOutcome
{
    /// <summary>The key is not a placed reference in this arm's order.</summary>
    public const string Absent = "absent";
    /// <summary>The probe's population definition excludes it; the rule is named (never counted by the coverage block).</summary>
    public const string Never = "never";
    /// <summary>Entered the population; a named, counted filter removed it (the rule equals a coverage-block key).</summary>
    public const string SetAside = "set_aside";
    /// <summary>Entered; a fact the judgement needs is missing (named).</summary>
    public const string Unjudgeable = "unjudgeable";
    /// <summary>Entered the population and was carried to judgement (a role-level event; pair events follow).</summary>
    public const string Entered = "entered";
    /// <summary>Measured: no relation at the probe's thresholds and outside its near band.</summary>
    public const string JudgedNoRelation = "judged_no_relation";
    /// <summary>Measured: a relation value exists but a NUMERIC cut excluded it; Values carry value, cut and margin.</summary>
    public const string JudgedBelowThreshold = "judged_below_threshold";
    /// <summary>Measured: a relation whose verdict is a correct non-clash (MovedTogether, ReSupported, Removed).</summary>
    public const string JudgedRelationNotClash = "judged_relation_not_clash";
    /// <summary>Measured: a relation with a clash verdict (the probe's own verdict in Detail).</summary>
    public const string JudgedRelation = "judged_relation";
}

public static class TraceRole
{
    public const string MovedHost = "moved-host";
    public const string Dependent = "dependent";
    public const string WinnerArm = "winner-arm-physical";
}

/// <summary>One step an object took. <paramref name="Other"/> is the pair partner for a pair event (the host, for a
/// dependent), else default. <paramref name="Values"/> carries the measured value and the cut for thresholds.</summary>
public sealed record TraceEvent(FormKey Key, string Role, string Outcome, string Rule, FormKey Other = default,
                                IReadOnlyDictionary<string, double>? Values = null, string? Detail = null);

public interface IProbeTraceSink
{
    /// <summary>Whether events for this key are kept. Counting mode wants every key.</summary>
    bool Wants(FormKey key);
    void Emit(TraceEvent e);
}

/// <summary>Keeps the events of the requested keys and counts every event by (role, outcome, rule). Keys = null is
/// counting mode: every event counted, none stored — the form the coverage-block consistency check runs in.</summary>
public sealed class ProbeTraceCollector : IProbeTraceSink
{
    readonly HashSet<FormKey>? _keys;
    public ProbeTraceCollector(IEnumerable<FormKey>? keys) => _keys = keys is null ? null : new HashSet<FormKey>(keys);
    public bool CountOnly => _keys is null;
    public List<TraceEvent> Events { get; } = new();
    public Dictionary<(string Role, string Outcome, string Rule), long> Counts { get; } = new();
    public bool Wants(FormKey key) => _keys is null || _keys.Contains(key);
    public void Emit(TraceEvent e)
    {
        var k = (e.Role, e.Outcome, e.Rule);
        Counts[k] = Counts.GetValueOrDefault(k) + 1;
        if (_keys is not null && _keys.Contains(e.Key)) Events.Add(e);
    }
    public IEnumerable<TraceEvent> For(FormKey key) => Events.Where(e => e.Key == key);
    public long Count(string role, string outcome, string rule) => Counts.GetValueOrDefault((role, outcome, rule));
}
