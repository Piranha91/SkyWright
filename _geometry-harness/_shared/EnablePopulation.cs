using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HarnessShared;

// THE PHYSICAL POPULATION — which placed references a run evaluates (Q27, 2026-09-06).
//
// A detector that reads geometry has to decide, for every reference, whether the thing is THERE when
// the cell loads. Until now the probes disagreed: the contact pass and the renderer resolved the XESP
// enable chain (R1, 2026-09-03), while the obstacle, floor, actor-band and overlap populations read
// the record's own initially-disabled flag and stopped. That flag is Creation Kit bookkeeping the
// engine ignores whenever a reference has an enable parent, so the two answers differ on tens of
// thousands of references in one order — and the difference is not academic: a room-layout variant
// switched off at load stood as the floor 48 u over a whole room's navmesh and moved verdicts (Q25).
//
// So: the default population of every physical read is the LOAD-TIME state — EnableState over the
// winning flags and XESP parents, chains followed, the player always enabled. The old flag-only
// population stays reachable, named, as `--flags`, so any number published before this can still be
// reproduced. A named world state (`--state`, roadmap 4.11) is the third case and is unchanged: it is
// the load-time population with some switches flipped.
//
// Two things this file exists to stop:
//   - a probe growing its own version of the rule. One policy, one CLI reader, one summary line.
//   - a run that cannot say what it judged. `Describe()` names the population on every run, and
//     `Unjudgeable` carries the unresolved-chain count into the emit's coverage block, so a clean
//     verdict cannot be given over a chain the run could not read.
//
// The NULL CONVENTION is deliberate and load-bearing: in `Flags` mode `Predicate` is null, and every
// consumer keeps the `enabled is null ? r.InitiallyDisabled : !enabled(key)` branch it already had.
// The legacy population is therefore produced by the same code that produced it before, not by a
// re-implementation that would have to be proven equal to it.

/// <summary>Which references a run treats as present. <see cref="LoadTime"/> resolves the XESP enable chain
/// (EnableState, the R1 rule) — the default since Q27; <see cref="Flags"/> reads each record's own
/// initially-disabled flag and nothing else — the population every run before 2026-09-06 judged.</summary>
public enum PopulationMode { LoadTime, Flags }

/// <summary>The enable facts of every reference a walk saw, gathered as that walk happens so a probe does not need a
/// second pass or a full <c>OverlapIndex</c> just to resolve a parent chain.
///
/// <para>Kept in two maps rather than one dictionary of <see cref="EnableFacts"/>: a byte of placement flags for every
/// reference, and the XESP statement only for the references that carry one. That is the compact form, and it keeps the
/// one distinction the fallback rests on — a reference that is PLACED and flag-enabled resolves <c>Resolved: true</c>,
/// while one the walk never saw resolves <c>enabled, Resolved: false</c>. Collapse the two and the unresolved count is
/// either nonsense or silence.</para>
///
/// <para>Semantics match <c>SwitchIndex.WinnerFacts</c> and <c>ContactIndex</c>'s winner facts exactly — a
/// deleted reference has no facts — so a probe that resolves its own chain and one that resolves it through the census
/// give the same answer. That equality is what makes the default run and an explicit <c>--state baseline</c> the same
/// run.</para></summary>
public sealed class EnableFactsIndex
{
    const byte Placed = 1, DisabledFlag = 2, DeletedFlag = 4;

    readonly Dictionary<FormKey, byte> _flags = new();
    readonly Dictionary<FormKey, (FormKey? Parent, bool Opposite)> _xesp = new();
    readonly HashSet<FormKey> _namedParents = new();

    /// <summary>References seen with a placement.</summary>
    public int Refs => _flags.Count;
    /// <summary>References whose winning record carries an XESP enable parent.</summary>
    public int WithParent => _xesp.Count;

    /// <summary>Distinct references named as an enable parent that no walked plugin places (or that a plugin deleted) —
    /// every chain through one of them resolves unresolved. Counted, never guessed at. The player reference (0x14) is
    /// not one of these: it has no placement record anywhere and EnableState answers for it directly.</summary>
    public int UnplacedParents
    {
        get
        {
            int n = 0;
            foreach (var p in _namedParents)
                if (p.ID != EnableState.PlayerRefId && (!_flags.TryGetValue(p, out var f) || (f & DeletedFlag) != 0)) n++;
            return n;
        }
    }

    /// <summary>Record one placed reference, in load order — a later plugin's record of the same reference replaces an
    /// earlier one, exactly as the winner does. An override that DROPS the XESP drops it here too.</summary>
    public void Note(PlacedRef r)
    {
        byte f = Placed;
        if (r.InitiallyDisabled) f |= DisabledFlag;
        if (r.Deleted) f |= DeletedFlag;
        _flags[r.FormKey] = f;
        if (r.EnableParent is { } p) { _xesp[r.FormKey] = (p, r.EnableParentOpposite); _namedParents.Add(p); }
        else _xesp.Remove(r.FormKey);
    }

    /// <summary>Record a deletion read off the major-record walk. A deleted reference usually loses its placement
    /// subrecord and never reaches <see cref="Note"/>, so a caller that walks records (as blockprobe and overlapprobe
    /// do) should call this for every <c>IPlacedGetter</c> it sees deleted — else a deleted parent reads as a live one.</summary>
    public void NoteDeleted(FormKey key)
    {
        _flags.TryGetValue(key, out var f);
        _flags[key] = (byte)(f | Placed | DeletedFlag);
    }

    /// <summary>The winner's enable facts for one reference; null when nothing places it or it is deleted.</summary>
    public EnableFacts? Facts(FormKey k)
    {
        if (!_flags.TryGetValue(k, out var f) || (f & DeletedFlag) != 0) return null;
        var (parent, opposite) = _xesp.TryGetValue(k, out var x) ? x : (null, false);
        return new EnableFacts((f & DisabledFlag) != 0, parent, opposite);
    }
}

/// <summary>The population one run evaluates: the mode, the state it names, the resolver every consumer takes, and the
/// counters the summary line and the emit's coverage block are built from.</summary>
public sealed class EnablePopulation
{
    readonly Func<FormKey, EnableFacts?>? _facts;
    readonly Dictionary<FormKey, EnableResolution> _memo = new();
    readonly HashSet<FormKey> _queried = new();
    long _disabled, _viaParent, _unresolved;
    readonly EnableFactsIndex? _index;

    EnablePopulation(PopulationMode mode, string stateName, Func<FormKey, EnableFacts?>? facts, EnableFactsIndex? index)
    { Mode = mode; StateName = stateName; _facts = facts; _index = index; }

    /// <summary>The name of the load-time state — the same string <c>StateArm.BaselineName</c> carries, spelled here
    /// so this file compiles against housecarl-core alone (a probe that never takes <c>--state</c> does not compile the
    /// census in). proofprobe pins the two equal.</summary>
    public const string BaselineStateName = "baseline";

    public PopulationMode Mode { get; }
    /// <summary>The world state this population is of: <see cref="BaselineStateName"/> for the load-time state, a <c>StateArm</c>'s
    /// name under <c>--state</c>, and <c>-</c> in flags mode (the flag population is not a world state).</summary>
    public string StateName { get; }

    /// <summary>The flag-only population: no chain. <see cref="Predicate"/> is null, so every consumer keeps the branch
    /// it already had and the pre-Q27 numbers come out of the same code that produced them. <paramref name="index"/> is
    /// only needed by a consumer that has a bare FormKey rather than a <c>PlacedRef</c> in hand (blockprobe's navcut
    /// boxes); without it <see cref="Enabled(FormKey)"/> in this mode throws rather than guessing.</summary>
    public static EnablePopulation Flags(EnableFactsIndex? index = null)
        => new(PopulationMode.Flags, "-", index is null ? null : index.Facts, index);

    /// <summary>The load-time population over a walk's own facts.</summary>
    public static EnablePopulation LoadTime(EnableFactsIndex index)
        => new(PopulationMode.LoadTime, BaselineStateName, index.Facts, index);

    /// <summary>The load-time population over a caller's facts — the census (<c>SwitchIndex.WinnerFacts</c>) or
    /// a state arm's wrap of it. <paramref name="stateName"/> is the arm's name.</summary>
    public static EnablePopulation Of(Func<FormKey, EnableFacts?> facts, string stateName)
        => new(PopulationMode.LoadTime, stateName, facts, null);

    /// <summary>Is this reference there? Prefer <see cref="Predicate"/> where the consumer holds the record itself —
    /// in flags mode that is null on purpose, so the consumer's own flag branch runs.</summary>
    public bool Enabled(FormKey k) => Resolution(k).Enabled;

    public EnableResolution Resolution(FormKey k)
    {
        if (Mode == PopulationMode.Flags)
        {
            if (_facts is null)
                throw new InvalidOperationException(
                    "the flags population was built without an EnableFactsIndex: read the record's own InitiallyDisabled "
                  + "flag (Predicate is null in this mode for exactly that reason), or build it with Flags(index).");
            var f = _facts(k);
            return new EnableResolution(f is null || !f.Value.InitiallyDisabled, false, f is not null);
        }
        if (_facts is null) throw new InvalidOperationException("a load-time population needs facts");
        if (!_queried.Add(k)) return EnableState.Resolve(k, _facts, _memo);
        var fresh = EnableState.Resolve(k, _facts, _memo);
        if (!fresh.Enabled) _disabled++;
        if (fresh.ViaParent) _viaParent++;
        if (!fresh.Resolved) _unresolved++;
        return fresh;
    }

    /// <summary>The predicate consumers take (<c>FloorFeed.Build</c>, <c>ActorBand.ForCell</c>,
    /// <c>OverlapIndex.Candidates</c>). NULL in flags mode — the consumer's own initially-disabled branch is the flag
    /// population, unchanged.</summary>
    public Func<FormKey, bool>? Predicate => Mode == PopulationMode.Flags ? null : Enabled;

    /// <summary>Distinct references the run asked about (load-time mode only).</summary>
    public long Queried => _queried.Count;
    /// <summary>Of those, how many are not there when the cell loads.</summary>
    public long DisabledAtLoad => _disabled;
    /// <summary>Of those, how many take their state from an enable parent rather than their own flag.</summary>
    public long ViaParent => _viaParent;
    /// <summary>Of those, how many rest on a chain the run could not read to the end — a parent nothing places, a
    /// parent a plugin deleted, or a cycle. The answer for these is <see cref="EnableState"/>'s stated fallback, not a
    /// measurement, and no clean verdict may rest on one.</summary>
    public long Unresolved => _unresolved;

    /// <summary>The line a probe prints once per run, so the output says which population produced it.</summary>
    public string Describe() => Mode == PopulationMode.Flags
        ? "population: FLAGS — each record's own initially-disabled flag, no enable chain resolved (the pre-Q27 default; "
        + "a reference with an XESP parent is judged on bookkeeping the engine ignores)"
        : $"population: LOAD-TIME state '{StateName}' — the XESP enable chain (EnableState, R1): "
        + $"{Queried:N0} references judged, {DisabledAtLoad:N0} not there at load ({ViaParent:N0} through a parent), "
        + $"{Unresolved:N0} on a chain that could not be read to the end"
        + (_index is { } ix ? $"; facts from {ix.Refs:N0} placements ({ix.WithParent:N0} with an XESP, {ix.UnplacedParents:N0} parents nothing places)" : "");

    /// <summary>The unresolved chains, for a <c>CoverageLine</c>'s Unjudgeable map — so <c>conflictdiff</c>'s
    /// coverage test sees a run whose chains went unread and refuses to credit it clean.</summary>
    public IReadOnlyDictionary<string, long> Unjudgeable =>
        Unresolved > 0 ? new Dictionary<string, long> { ["EnableChainUnresolved"] = Unresolved }
                       : new Dictionary<string, long>();

    /// <summary>The population identity carried in an emit (<c>ConflictFile.Population</c>), so a later diff can tell
    /// two runs of the same probe apart.</summary>
    public string Identity => Mode == PopulationMode.Flags ? "flags" : "load-time";
}

/// <summary>The one CLI reader for <c>--flags</c> and <c>--state</c>: which combinations a probe supports, and the
/// message it prints when a caller names one it does not. Every rejection is explicit — a probe never silently treats
/// an unsupported option as the default, and <c>--state flags</c> is not special-cased (it looks for a switch called
/// "flags", finds none, and is refused by <c>StateArm.Parse</c>, which is the right answer: "flags" is not a
/// reserved switch name).</summary>
public static class PopulationCli
{
    public const string FlagsHelp =
        "--flags   judge the flag-only population: each record's own initially-disabled flag, no enable chain (the "
      + "default before 2026-09-06; kept so a number published then can be reproduced). Not with --state.";

    /// <summary>Settle the two options against what this probe supports. Returns the mode, or throws
    /// <see cref="ArgumentException"/> with the exact message to print.</summary>
    /// <param name="stateSupported">false for a probe that does not build the switch census a named state needs — a
    /// calibration reading one source plugin (navprobe, corridorprobe, floorgapprobe), or a pass that reads the order
    /// but never ingests it into the shared index (navownprobe). Refused rather than approximated: a calibration must
    /// not quietly become an active-order run, and a state nobody can name is not a state.</param>
    public static PopulationMode Resolve(string probe, bool flagsRequested, string? stateSpec,
                                         bool flagsSupported = true, bool stateSupported = true)
    {
        if (flagsRequested && stateSpec is not null)
            throw new ArgumentException("--flags and --state name the two populations; give one or the other. "
                                      + "--flags is the flag-only analysis; --state baseline is the load-time state, which is already the default.");
        if (stateSpec is not null && !stateSupported)
            throw new ArgumentException($"{probe} builds no switch census, so there is nothing here to flip: --state needs one "
                                      + "(blockprobe, overlapprobe, contactprobe, stateprobe and viewprobe build it). "
                                      + $"{probe}'s default is already the load-time state of its own source.");
        if (flagsRequested && !flagsSupported)
            throw new ArgumentException($"{probe} has always resolved the enable chain, so there is no flag-only population of it to "
                                      + "reproduce and --flags would be a new mode known to be wrong about the game. Its default is the "
                                      + "load-time state; --state names another world state.");
        return flagsRequested ? PopulationMode.Flags : PopulationMode.LoadTime;
    }
}
