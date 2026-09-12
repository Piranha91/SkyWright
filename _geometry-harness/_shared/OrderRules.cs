using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarnessShared;

// ORDERING RULES — what a load order is ALLOWED to be, and where each rule came from (roadmap 4.20, 2026-09-11).
//
// SyntheticOrder can switch plugins on and off at the positions MO2 already gave them. It cannot reorder, and it
// says why: "position decides which plugin wins a record, so inventing one would quietly invent the answer too."
// This file is the other half of that sentence. A position may be changed when something SAYS it may — and the
// something is either a rule the FILES prove (a master loads before the plugin that declares it) or a rule a
// person wrote down with a source attached. Those two are kept apart on purpose, because they fail differently:
// an engine rule broken means the game will not load the order as written; an author's advice broken means a mod
// may misbehave in a way nothing here can see.
//
// THREE HARDNESSES, and only the first one is legality:
//
//   required    the order MUST satisfy it. Engine rules are always required and are DERIVED, never typed: the
//               declared masters, the master/non-master grouping, the base-game masters' fixed positions. A
//               required rule from the input file joins them and is checked the same way.
//   advice      an author or a sorting rule says this order is better. Carries PROVENANCE or it is refused at
//               read time — an anonymous "load A before B" is a rumour, and a tool that launders rumours into
//               constraints is worse than one that has none. A candidate that violates advice is still a
//               candidate; the violation is reported against it.
//   preference  the user's taste. Never legality, never a refusal, always reported.
//
// NOTHING IS EVER DROPPED TO MAKE AN ORDER SORTABLE. A cycle among the mandatory rules is a REFUSAL with the
// cycle spelled out edge by edge; advice that cannot be satisfied is reported as unsatisfiable WITH its
// provenance and the candidates are generated anyway. The roadmap's wording is the contract: "Detect
// contradictory constraints and explain the cycle; never silently discard a rule to make the order sortable."
//
// ---------------------------------------------------------------------------------------------------------
// THE RULES FILE (--rules FILE). BCL JSON, schema version 1:
//
//   {
//     "schemaVersion": 1,
//     "fixed": ["Skyrim.esm", "MyBashedPatch.esp"],
//     "rules": [
//       {
//         "before": "CityOverhaul.esp",
//         "after":  "CityOverhaulCompatibilityPatch.esp",
//         "hardness": "advice",
//         "reason": "the patch expects to win the cell records the two share",
//         "provenance": {
//           "source":       "https://www.example.invalid/mods/1234",
//           "checked":      "2026-09-11",
//           "versionScope": "patch 2.10.2",
//           "note":         "provisional — the author's own wording has not been re-read"
//         }
//       }
//     ]
//   }
//
//   fixed     plugins whose position RELATIVE TO EVERY OTHER PLUGIN must not change. Hardness required. This is
//             how a user pins "I sorted this by hand, leave it alone" without pinning the whole order.
//   before /  plugin FILENAMES as loadorder.txt spells them. "before" loads earlier (lower priority, loses a
//   after     contested record to "after").
//   hardness  "required" | "advice" | "preference".
//   provenance  REQUIRED for advice. source (a URL or a file), checked (a date), versionScope (what the advice
//             was true OF), note (optional). A required or preference rule may omit it, and the ruleset then
//             carries a note saying that rule is anonymous — it is not silently equivalent to a sourced one.
//   reason    free text for the report. Never load-bearing.
//
// A rule naming a plugin that is not in the order is INAPPLICABLE, not violated, and says so by name.
// ---------------------------------------------------------------------------------------------------------

public enum RuleHardness { Required, Advice, Preference }

/// <summary>Where a rule came from, and what it was true of. The roadmap's "preserve provenance and distinguish
/// mandatory constraints from preferences or uncertain advice" is this record plus <see cref="RuleHardness"/>.</summary>
public sealed record RuleProvenance(string Source, string Checked, string VersionScope, string? Note = null)
{
    public string Line =>
        $"{Source} (checked {Checked}; scope: {VersionScope})" + (string.IsNullOrWhiteSpace(Note) ? "" : $" — {Note}");
}

/// <summary>One ordering rule: <paramref name="Before"/> loads earlier than <paramref name="After"/>.
/// <paramref name="Origin"/> names WHO says so — an <see cref="EngineRule"/> constant for a rule derived from the
/// files, or "rules file &lt;path&gt; #n" for one that was typed.</summary>
public sealed record OrderRule(
    string Before,
    string After,
    RuleHardness Hardness,
    string Origin,
    RuleProvenance? Provenance = null,
    string? Reason = null)
{
    /// <summary>The rule as a reader can quote it back. Every violation and every cycle edge is reported by this.</summary>
    public string Name => $"{Hardness.ToString().ToLowerInvariant()}: '{Before}' before '{After}' [{Origin}]";

    public string Full =>
        Name + (Reason is { Length: > 0 } ? $" — {Reason}" : "")
             + (Provenance is null ? "" : $" — provenance: {Provenance.Line}");
}

/// <summary>The rules the FILES prove. Spelled once so a violation, a contradiction and a report all quote the
/// same sentence, and so a reader can grep for one.</summary>
public static class EngineRule
{
    public const string Masters = "engine: a plugin's declared masters load before it";
    public const string Present = "engine: every declared master is loaded";
    public const string Groups = "engine: master-flagged plugins load before every plugin that is not one";
    public const string BaseMasters = "engine: the base-game masters keep their positions, in their own order";
    public const string OnePosition = "engine: one plugin has one position in the order";

    /// <summary>The base-game masters, in the order the engine loads them. A plugin absent from the order is
    /// simply skipped — the rule is about the ones that are present.</summary>
    public static readonly string[] BaseOrder =
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };
}

/// <summary>One broken rule, named, with enough detail to act on and the provenance of whoever asked for it.</summary>
public sealed record RuleViolation(string Rule, string Detail, string? Provenance = null)
{
    public override string ToString() =>
        $"{Rule} — {Detail}" + (Provenance is null ? "" : $" [provenance: {Provenance}]");
}

/// <summary>Whether an order is legal, and if not, EVERY hard rule it breaks. Never a bare false: a caller that
/// only learns "no" cannot tell the user what to move.</summary>
public sealed record LegalityResult(bool Legal, IReadOnlyList<RuleViolation> Violations, IReadOnlyList<string> Inapplicable)
{
    public string Summary => Legal
        ? "legal" + (Inapplicable.Count > 0 ? $" ({Inapplicable.Count} rule(s) inapplicable)" : "")
        : $"ILLEGAL — {Violations.Count} rule(s) violated: {string.Join("; ", Violations.Select(v => v.Rule))}";
}

public enum ContradictionKind
{
    /// <summary>A cycle among the mandatory rules. No order satisfies them; the search refuses.</summary>
    HardCycle,
    /// <summary>Advice or a preference that the mandatory rules make impossible. Reported, never dropped.</summary>
    SoftAgainstHard,
    /// <summary>Advice or preferences that contradict each other. Reported, never dropped.</summary>
    SoftCycle,
}

/// <summary>A contradiction among the rules, with every rule in it named and its provenance carried.</summary>
public sealed record Contradiction(ContradictionKind Kind, IReadOnlyList<OrderRule> Rules, string Explanation)
{
    public string Key => Kind + "|" + string.Join("|", Rules.Select(r => r.Name).OrderBy(s => s, StringComparer.Ordinal));
}

// ---------------------------------------------------------------------------------------------------------
//  The rules file, as JSON shapes. Deserialised into these, validated, then turned into OrderRule.
// ---------------------------------------------------------------------------------------------------------

public sealed record ProvenanceSpec(string? Source, string? Checked, string? VersionScope, string? Note);

public sealed record RuleSpec(string? Before, string? After, string? Hardness, ProvenanceSpec? Provenance, string? Reason);

public sealed record RulesFile(int SchemaVersion, IReadOnlyList<string>? Fixed, IReadOnlyList<RuleSpec>? Rules);

/// <summary>What a rules file parsed to: the rules, the fixed set, and the notes about what the file did NOT
/// carry (an anonymous required rule, a duplicate collapsed).</summary>
public sealed record ParsedRules(IReadOnlyList<OrderRule> Rules, IReadOnlyList<string> Fixed, IReadOnlyList<string> Notes);

public static class OrderRules
{
    public const int CurrentSchema = 1;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parse a rules file's TEXT. Refusals throw <see cref="InvalidDataException"/> with the reason and the
    /// rule index — a rules file that cannot be trusted is not partially applied.</summary>
    public static ParsedRules Parse(string json, string origin)
    {
        RulesFile? f;
        try { f = JsonSerializer.Deserialize<RulesFile>(json, Json); }
        catch (JsonException ex) { throw new InvalidDataException($"{origin}: not readable as JSON — {ex.Message}"); }
        if (f is null) throw new InvalidDataException($"{origin}: empty rules file; refused rather than read as 'no rules'");
        if (f.SchemaVersion != CurrentSchema)
            throw new InvalidDataException($"{origin}: rules schema {f.SchemaVersion} is not {CurrentSchema}; refused rather than guessed at");

        var notes = new List<string>();
        var fixedList = new List<string>();
        foreach (var p in f.Fixed ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(p)) throw new InvalidDataException($"{origin}: 'fixed' contains an empty name");
            if (fixedList.Contains(p, StringComparer.OrdinalIgnoreCase)) { notes.Add($"'fixed' lists '{p}' twice; counted once"); continue; }
            fixedList.Add(p.Trim());
        }

        var rules = new List<OrderRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var specs = f.Rules ?? Array.Empty<RuleSpec>();
        for (int i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            string where = $"{origin} rule #{i}";
            if (string.IsNullOrWhiteSpace(s.Before) || string.IsNullOrWhiteSpace(s.After))
                throw new InvalidDataException($"{where}: needs both 'before' and 'after'");
            var before = s.Before!.Trim();
            var after = s.After!.Trim();
            if (before.Equals(after, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{where}: '{before}' cannot load before itself");

            var hardness = (s.Hardness ?? "").Trim().ToLowerInvariant() switch
            {
                "required" => RuleHardness.Required,
                "advice" => RuleHardness.Advice,
                "preference" => RuleHardness.Preference,
                "" => throw new InvalidDataException($"{where}: no 'hardness'. Say which of required / advice / preference this is — the three are checked differently and a missing one cannot be guessed."),
                var other => throw new InvalidDataException($"{where}: hardness '{other}' is not one of required / advice / preference"),
            };

            RuleProvenance? prov = null;
            var ps = s.Provenance;
            if (ps is not null && !(string.IsNullOrWhiteSpace(ps.Source) && string.IsNullOrWhiteSpace(ps.Checked) && string.IsNullOrWhiteSpace(ps.VersionScope)))
            {
                if (string.IsNullOrWhiteSpace(ps.Source) || string.IsNullOrWhiteSpace(ps.Checked) || string.IsNullOrWhiteSpace(ps.VersionScope))
                    throw new InvalidDataException(
                        $"{where}: provenance needs all of source, checked and versionScope. A half-filled provenance is " +
                        "harder to judge than none, because it looks sourced.");
                prov = new RuleProvenance(ps.Source!.Trim(), ps.Checked!.Trim(), ps.VersionScope!.Trim(),
                                          string.IsNullOrWhiteSpace(ps.Note) ? null : ps.Note!.Trim());
            }

            if (hardness == RuleHardness.Advice && prov is null)
                throw new InvalidDataException(
                    $"{where}: advice needs provenance (source, checked, versionScope). Advice without a source is a " +
                    "rumour, and this tool will not launder one into a constraint. Say who advises it and what version " +
                    "it was true of, or file it as a preference.");

            if (prov is null && hardness != RuleHardness.Advice)
                notes.Add($"{where} ('{before}' before '{after}', {hardness.ToString().ToLowerInvariant()}) carries no provenance — it is anonymous, not sourced");

            var rule = new OrderRule(before, after, hardness, $"{origin} #{i}", prov,
                                     string.IsNullOrWhiteSpace(s.Reason) ? null : s.Reason!.Trim());
            var key = $"{hardness}|{before}|{after}";
            if (!seen.Add(key)) { notes.Add($"{where} repeats an earlier identical rule; counted once"); continue; }
            rules.Add(rule);
        }

        return new ParsedRules(rules, fixedList, notes);
    }

    public static ParsedRules ParseFile(string path) => Parse(File.ReadAllText(path), $"rules file '{Path.GetFileName(path)}'");
}

/// <summary>The rules a particular order is judged against: the engine's, derived from the headers, plus the
/// input file's. Built once per run; <see cref="Check"/> is then cheap enough to call per candidate.</summary>
public sealed class OrderRuleSet
{
    public IReadOnlyList<string> Baseline { get; }
    public IReadOnlyDictionary<string, PluginHeader?> Headers { get; }
    public IReadOnlyList<string> Fixed { get; }
    public IReadOnlyList<OrderRule> Input { get; }

    /// <summary>The engine rules this order's files prove — one per (master, dependent) pair. The grouping and
    /// base-master rules are not enumerated pair by pair (that is quadratic and unreadable); they are checked
    /// structurally in <see cref="Check"/> and edge by edge in the contradiction pass.</summary>
    public IReadOnlyList<OrderRule> EngineRules { get; }

    /// <summary>Cycles and structural impossibilities among the MANDATORY rules. Non-empty means no order
    /// satisfies the constraints as encoded, and the search must refuse rather than pick a winner.</summary>
    public IReadOnlyList<Contradiction> Contradictions { get; }

    /// <summary>Advice and preferences the mandatory rules make impossible, or that contradict each other. Reported
    /// with provenance, never dropped; candidates are still generated.</summary>
    public IReadOnlyList<Contradiction> UnsatisfiableSoft { get; }

    /// <summary>What the ruleset knows it did not model for this run: an unreadable header, a rule naming a plugin
    /// that is not in the order, an anonymous rule.</summary>
    public IReadOnlyList<string> Notes { get; }

    public bool Satisfiable => Contradictions.Count == 0;

    readonly Dictionary<string, int> _basePos;
    readonly HashSet<string> _fixedSet;
    readonly HashSet<string> _present;

    OrderRuleSet(IReadOnlyList<string> baseline, IReadOnlyDictionary<string, PluginHeader?> headers,
                 IReadOnlyList<string> fixedPlugins, IReadOnlyList<OrderRule> input, IReadOnlyList<OrderRule> engine,
                 IReadOnlyList<Contradiction> contradictions, IReadOnlyList<Contradiction> unsatisfiableSoft,
                 IReadOnlyList<string> notes)
    {
        Baseline = baseline; Headers = headers; Fixed = fixedPlugins; Input = input; EngineRules = engine;
        Contradictions = contradictions; UnsatisfiableSoft = unsatisfiableSoft; Notes = notes;
        _basePos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < baseline.Count; i++) _basePos[baseline[i]] = i;
        _fixedSet = new HashSet<string>(fixedPlugins, StringComparer.OrdinalIgnoreCase);
        _present = new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------------------------------------
    //  Build
    // -----------------------------------------------------------------------------------------------------

    /// <param name="baseline">The user's order, load order, winner last. Every rule is judged relative to it.</param>
    /// <param name="headers">name → header, or null for a plugin whose header could not be read. A null header makes
    /// NO claim: its master rules are skipped and the skip is noted, never read as "declares no masters".</param>
    public static OrderRuleSet Build(IReadOnlyList<string> baseline,
                                     IReadOnlyDictionary<string, PluginHeader?> headers,
                                     IEnumerable<string> fixedPlugins,
                                     IEnumerable<OrderRule> input)
    {
        var notes = new List<string>();
        var order = baseline.ToList();
        var present = new HashSet<string>(order, StringComparer.OrdinalIgnoreCase);
        var fixedList = fixedPlugins.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var inputList = input.ToList();

        foreach (var f in fixedList)
            if (!present.Contains(f))
                notes.Add($"'fixed' names '{f}', which is not in the order — nothing to hold in place");
        foreach (var r in inputList)
        {
            if (!present.Contains(r.Before)) notes.Add($"{r.Name}: '{r.Before}' is not in the order — the rule is inapplicable, not violated");
            if (!present.Contains(r.After)) notes.Add($"{r.Name}: '{r.After}' is not in the order — the rule is inapplicable, not violated");
        }
        foreach (var name in order)
            if (!headers.TryGetValue(name, out var h) || h is null)
                notes.Add($"'{name}': header unreadable — its master and flag rules are SKIPPED for this run, not assumed away");

        // ---- the engine's own rules, one per declared (master, dependent) pair ---------------------------
        var engine = new List<OrderRule>();
        foreach (var name in order)
        {
            if (!headers.TryGetValue(name, out var h) || h is null) continue;
            foreach (var m in h.Masters)
            {
                if (!present.Contains(m)) continue;      // a missing master is a legality violation, not a rule
                engine.Add(new OrderRule(m, name, RuleHardness.Required, EngineRule.Masters, null,
                                         $"'{name}' declares '{m}' in its master table"));
            }
        }
        // the base-game masters' own sequence, for the plugins that are actually present
        var basePresent = EngineRule.BaseOrder.Where(present.Contains).ToList();
        for (int i = 0; i + 1 < basePresent.Count; i++)
            engine.Add(new OrderRule(basePresent[i], basePresent[i + 1], RuleHardness.Required, EngineRule.BaseMasters, null,
                                     "the engine loads the base-game masters in this order"));

        // ---- the graph the contradiction pass runs on ----------------------------------------------------
        // Materialised: master declarations, the base-master chain, every fixed plugin against every other plugin,
        // and the input's required rules. NOT materialised: the master/non-master grouping (quadratic). That one is
        // caught edge by edge instead, and exactly: the grouping is a RANK, so any chain of rules that carries a
        // non-master ahead of a master must contain one edge that does it, and EdgeAgainstStructure sees that edge.
        var hardEdges = new List<Edge>();
        foreach (var e in engine) hardEdges.Add(new Edge(e.Before, e.After, e));
        foreach (var f in fixedList)
        {
            if (!present.Contains(f)) continue;
            int fi = order.FindIndex(n => n.Equals(f, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < order.Count; i++)
            {
                if (i == fi) continue;
                var other = order[i];
                var rule = new OrderRule(i < fi ? other : f, i < fi ? f : other, RuleHardness.Required, $"fixed: '{f}'", null,
                                         $"the rules file fixes '{f}', so its order against '{other}' is the user's");
                hardEdges.Add(new Edge(rule.Before, rule.After, rule));
            }
        }
        foreach (var r in inputList.Where(r => r.Hardness == RuleHardness.Required))
        {
            if (!present.Contains(r.Before) || !present.Contains(r.After)) continue;
            hardEdges.Add(new Edge(r.Before, r.After, r));
        }

        var contradictions = new List<Contradiction>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        void Add(List<Contradiction> into, Contradiction c) { if (seenKeys.Add(c.Key)) into.Add(c); }

        bool IsMaster(string n) => headers.TryGetValue(n, out var h) && h is { MasterFlag: true };
        bool KnownFlags(string n) => headers.TryGetValue(n, out var h) && h is not null;
        int BaseIdx(string n) => Array.FindIndex(EngineRule.BaseOrder, b => b.Equals(n, StringComparison.OrdinalIgnoreCase));

        // structural conflicts, edge by edge
        foreach (var e in hardEdges.Where(e => e.Rule.Origin != EngineRule.BaseMasters))
        {
            var c = EdgeAgainstStructure(e, IsMaster, KnownFlags, BaseIdx, ContradictionKind.HardCycle);
            if (c is not null) Add(contradictions, c);
        }
        // cycles among the mandatory rules
        foreach (var cycle in FindCycles(hardEdges, 20))
            Add(contradictions, new Contradiction(ContradictionKind.HardCycle, cycle.Select(e => e.Rule).ToList(), ExplainCycle(cycle)));

        // ---- advice and preferences: reported, never dropped --------------------------------------------
        var soft = inputList.Where(r => r.Hardness != RuleHardness.Required)
                            .Where(r => present.Contains(r.Before) && present.Contains(r.After)).ToList();
        var unsat = new List<Contradiction>();
        foreach (var r in soft)
        {
            var e = new Edge(r.Before, r.After, r);
            var c = EdgeAgainstStructure(e, IsMaster, KnownFlags, BaseIdx, ContradictionKind.SoftAgainstHard);
            if (c is not null) Add(unsat, c);
        }
        var both = new List<Edge>(hardEdges);
        both.AddRange(soft.Select(r => new Edge(r.Before, r.After, r)));
        foreach (var cycle in FindCycles(both, 20))
        {
            if (!cycle.Any(e => e.Rule.Hardness != RuleHardness.Required)) continue;   // already reported as a hard cycle
            var kind = cycle.All(e => e.Rule.Hardness != RuleHardness.Required)
                ? ContradictionKind.SoftCycle : ContradictionKind.SoftAgainstHard;
            Add(unsat, new Contradiction(kind, cycle.Select(e => e.Rule).ToList(), ExplainCycle(cycle)));
        }

        return new OrderRuleSet(order, headers, fixedList, inputList, engine, contradictions, unsat, notes);
    }

    /// <summary>A single edge judged against the two structural rules that are ranks rather than pairs: the
    /// master/non-master grouping and the base masters' fixed positions. Returns null when the edge is fine.</summary>
    static Contradiction? EdgeAgainstStructure(Edge e, Func<string, bool> isMaster, Func<string, bool> knownFlags,
                                               Func<string, int> baseIdx, ContradictionKind kind)
    {
        if (knownFlags(e.From) && knownFlags(e.To) && !isMaster(e.From) && isMaster(e.To))
            return new Contradiction(kind, new[] { e.Rule },
                $"{e.Rule.Full} — but '{e.From}' is not master-flagged and '{e.To}' is, and {EngineRule.Groups}. " +
                "The engine would load them the other way round, so the rule can never hold.");

        int fi = baseIdx(e.From), ti = baseIdx(e.To);
        if (ti >= 0 && fi < 0)
            return new Contradiction(kind, new[] { e.Rule },
                $"{e.Rule.Full} — but '{e.To}' is a base-game master and '{e.From}' is not, and {EngineRule.BaseMasters}.");
        if (ti >= 0 && fi >= 0 && fi > ti)
            return new Contradiction(kind, new[] { e.Rule },
                $"{e.Rule.Full} — but both are base-game masters and the engine loads '{e.To}' before '{e.From}'. " +
                EngineRule.BaseMasters + ".");
        return null;
    }

    static string ExplainCycle(IReadOnlyList<Edge> cycle)
    {
        var sb = new StringBuilder();
        sb.Append("these rules form a cycle, so no order satisfies all of them:\n");
        foreach (var e in cycle) sb.Append($"      {e.From} -> {e.To}   {e.Rule.Full}\n");
        sb.Append($"      ...and {cycle[^1].To} is {cycle[0].From} again.");
        return sb.ToString();
    }

    // -----------------------------------------------------------------------------------------------------
    //  Legality
    // -----------------------------------------------------------------------------------------------------

    /// <summary>Every HARD rule <paramref name="candidate"/> breaks — engine rules and the input's required ones.
    /// Advice and preferences are not legality; see <see cref="ViolatedSoft"/>.</summary>
    public LegalityResult Check(IReadOnlyList<string> candidate)
    {
        var v = new List<RuleViolation>();
        var inapplicable = new List<string>();

        var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < candidate.Count; i++)
        {
            if (!pos.TryAdd(candidate[i], i))
                v.Add(new RuleViolation(EngineRule.OnePosition,
                    $"'{candidate[i]}' appears twice (positions {pos[candidate[i]]} and {i}). One plugin has one position; " +
                    "an order that lists it twice is not a load order."));
        }
        foreach (var n in Baseline)
            if (!pos.ContainsKey(n))
                v.Add(new RuleViolation(EngineRule.OnePosition, $"'{n}' is in the user's order but not in this candidate — a candidate reorders, it does not drop plugins."));
        foreach (var n in candidate)
            if (!_present.Contains(n))
                v.Add(new RuleViolation(EngineRule.OnePosition, $"'{n}' is in this candidate but not in the user's order — a candidate reorders, it does not add plugins."));

        // the base-game masters: their own sequence, and ahead of everything that is not one
        var basePresent = EngineRule.BaseOrder.Where(b => pos.ContainsKey(b)).ToList();
        for (int i = 0; i + 1 < basePresent.Count; i++)
            if (pos[basePresent[i]] > pos[basePresent[i + 1]])
                v.Add(new RuleViolation(EngineRule.BaseMasters,
                    $"'{basePresent[i]}' is at {pos[basePresent[i]]}, after '{basePresent[i + 1]}' at {pos[basePresent[i + 1]]}; the engine loads them the other way."));
        foreach (var b in basePresent)
        {
            var ahead = candidate.Take(pos[b]).Where(n => Array.FindIndex(EngineRule.BaseOrder, x => x.Equals(n, StringComparison.OrdinalIgnoreCase)) < 0).ToList();
            if (ahead.Count > 0)
                v.Add(new RuleViolation(EngineRule.BaseMasters,
                    $"'{b}' is at {pos[b]}, behind {ahead.Count} plugin(s) that are not base-game masters (first: '{ahead[0]}'). The engine loads the base masters first."));
        }

        // the master / non-master grouping
        foreach (var name in candidate)
        {
            if (!Headers.TryGetValue(name, out var h) || h is null || h.MasterFlag) continue;
            var mastersAfter = candidate.Skip(pos[name] + 1)
                                        .Where(n => Headers.TryGetValue(n, out var hh) && hh is { MasterFlag: true }).ToList();
            if (mastersAfter.Count == 0) continue;
            v.Add(new RuleViolation(EngineRule.Groups,
                $"'{name}' (not master-flagged, position {pos[name]}) loads ahead of master-flagged '{mastersAfter[0]}' (position {pos[mastersAfter[0]]})" +
                (mastersAfter.Count > 1 ? $" and {mastersAfter.Count - 1} more" : "") +
                ". The engine sorts masters first, so this is not the order the game would use."));
        }

        // declared masters
        foreach (var name in candidate)
        {
            if (!Headers.TryGetValue(name, out var h) || h is null) continue;
            foreach (var m in h.Masters)
            {
                if (!pos.TryGetValue(m, out int mi))
                {
                    v.Add(new RuleViolation(EngineRule.Present, $"'{name}' declares master '{m}', which is not in the order. The game cannot load it."));
                    continue;
                }
                if (mi > pos[name])
                    v.Add(new RuleViolation(EngineRule.Masters, $"'{m}' is a declared master of '{name}' but loads after it (positions {mi} and {pos[name]})."));
            }
        }

        // the fixed set
        foreach (var f in Fixed)
        {
            if (!pos.TryGetValue(f, out int fi) || !_basePos.TryGetValue(f, out int fb)) continue;
            var moved = new List<string>();
            foreach (var other in Baseline)
            {
                if (other.Equals(f, StringComparison.OrdinalIgnoreCase)) continue;
                if (!pos.TryGetValue(other, out int oi)) continue;
                if ((_basePos[other] < fb) != (oi < fi)) moved.Add(other);
            }
            if (moved.Count > 0)
                v.Add(new RuleViolation($"fixed: '{f}'",
                    $"'{f}' changed position relative to {moved.Count} plugin(s) (first: '{moved[0]}'). The rules file fixes it."));
        }

        // the input's required rules
        foreach (var r in Input.Where(r => r.Hardness == RuleHardness.Required))
        {
            if (!pos.TryGetValue(r.Before, out int bi) || !pos.TryGetValue(r.After, out int ai))
            { inapplicable.Add($"{r.Name}: names a plugin that is not in the order"); continue; }
            if (bi > ai)
                v.Add(new RuleViolation(r.Name, $"'{r.Before}' is at {bi}, after '{r.After}' at {ai}." + (r.Reason is { Length: > 0 } ? $" {r.Reason}" : ""),
                                        r.Provenance?.Line));
        }

        return new LegalityResult(v.Count == 0, v, inapplicable);
    }

    /// <summary>The advice and preference rules <paramref name="candidate"/> violates. Not legality — a candidate that
    /// breaks advice is still offered, with the breakage named against it.</summary>
    public IReadOnlyList<OrderRule> ViolatedSoft(IReadOnlyList<string> candidate)
    {
        var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < candidate.Count; i++) pos.TryAdd(candidate[i], i);
        var outv = new List<OrderRule>();
        foreach (var r in Input.Where(r => r.Hardness != RuleHardness.Required))
        {
            if (!pos.TryGetValue(r.Before, out int bi) || !pos.TryGetValue(r.After, out int ai)) continue;
            if (bi > ai) outv.Add(r);
        }
        return outv;
    }

    // -----------------------------------------------------------------------------------------------------
    //  The cycle finder — plain DFS, deterministic, edges carried so the explanation can quote each rule.
    // -----------------------------------------------------------------------------------------------------

    internal sealed record Edge(string From, string To, OrderRule Rule);

    internal static List<List<Edge>> FindCycles(IReadOnlyList<Edge> edges, int max)
    {
        var adj = new Dictionary<string, List<Edge>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in edges)
        {
            if (!adj.TryGetValue(e.From, out var l)) adj[e.From] = l = new List<Edge>();
            l.Add(e);
            if (!adj.ContainsKey(e.To)) adj[e.To] = new List<Edge>();
        }
        foreach (var l in adj.Values)
            l.Sort((a, b) => string.CompareOrdinal(a.To + "|" + a.Rule.Name, b.To + "|" + b.Rule.Name));

        var colour = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // 0 white, 1 grey, 2 black
        var stack = new List<Edge>();
        var onStack = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);  // node → depth in stack
        var found = new List<List<Edge>>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string n)
        {
            if (found.Count >= max) return;
            colour[n] = 1;
            onStack[n] = stack.Count;
            foreach (var e in adj[n])
            {
                if (found.Count >= max) break;
                colour.TryGetValue(e.To, out int c);
                if (c == 1)
                {
                    var cycle = stack.Skip(onStack[e.To]).Append(e).ToList();
                    var key = CanonicalKey(cycle);
                    if (keys.Add(key)) found.Add(cycle);
                    continue;
                }
                if (c == 2) continue;
                stack.Add(e);
                Visit(e.To);
                stack.RemoveAt(stack.Count - 1);
            }
            onStack.Remove(n);
            colour[n] = 2;
        }

        foreach (var n in adj.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            colour.TryGetValue(n, out int c);
            if (c == 0) Visit(n);
        }
        return found;
    }

    static string CanonicalKey(IReadOnlyList<Edge> cycle)
    {
        var names = cycle.Select(e => e.Rule.Name + ">" + e.To).ToList();
        int best = 0;
        for (int i = 1; i < names.Count; i++) if (string.CompareOrdinal(names[i], names[best]) < 0) best = i;
        return string.Join("|", Enumerable.Range(0, names.Count).Select(k => names[(best + k) % names.Count]));
    }
}
