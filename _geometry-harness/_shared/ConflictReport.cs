using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarnessShared;

// The handoff between a DETECTOR and the PATCH VERIFIER.
//
// patchprobe's original answer was "a plugin exists that masters both of these mods". That is a
// statement about a relationship, not about a repair: RRSC-SurWR-Patch.esp masters USSEP and SurWR
// and is a children's-appearance patch, so it reconciles those two mods about something else
// entirely. The detectors already know the exact records that are broken. Handing those records to
// the verifier is what turns "a patch exists" into "a patch touches the thing that is broken".
//
// Deliberately a file rather than a library call: the detectors are slow (7s and ~40s) and the
// verifier is slower (64s to index the machine). Keeping them separate means a conflict set can be
// re-verified without re-detecting, and the emitted file is itself a durable artifact for _runs\.
//
// THE PHYSICAL PREDICATE (2026-09-03). Every class the detectors emit has a physical half and a
// provenance half — a navmesh clobber is "the geometry differs" AND "the winner declares no dependency";
// an overlap row is "the boxes intersect" AND "the later plugin introduced it blind"; a contact row is
// "the dependent touches nothing" AND "the mover's side did not re-place it". A sham patch that masters
// both plugins and re-states the winner's record flips the provenance half and leaves the physical one
// untouched, and a diff that credits a row's disappearance as repair credits the sham (the 2026-09-02
// review's risk; reproduced on the lab 2026-09-03 — three detectors, four cells). So each emitted record
// carries `Physical`: whether the class's OWN physical half holds under the arm it was emitted from,
// with the number it was read from in `PhysicalDetail`. A probe run with `--base <emit>` also re-measures
// every record of that emit under ITS arm and writes the results as `Predicates`, so `conflictdiff` can
// credit a vanished row as REPAIRED only when the predicate went Holds -> Gone, and call the rest
// RECLASSIFIED. `Coverage` carries what each probe judged and could not judge, so a consumer can tell a
// clean order from an unreadable one. The decisions are DetectorModel.Credit / DifferentialVerdict /
// CoverageRefusals, proofprobe-owned; this file is the format.

/// <summary>One record the fix would have to touch, and what part it plays in the conflict.</summary>
/// <param name="FormKey">Round-trips through FormKey.ToString() / FormKey.Factory.</param>
/// <param name="Role">e.g. "navmesh", "door", "quest", "alias-ref" — reporting only, never matched on.</param>
/// <param name="Label">Human context, e.g. the cell name. Never load-bearing.</param>
/// <param name="Physical">The class's physical half under the emitting arm (see the file comment). Null in an
/// emit written before 2026-09-03; a reader treats null as Unmeasured and says so.</param>
/// <param name="PhysicalDetail">The number the predicate was read from, in words. Never load-bearing.</param>
public sealed record ConflictRecord(string FormKey, string Role, string? Label, PhysicalState? Physical = null, string? PhysicalDetail = null);

/// <summary>One detected conflict between two plugins, with the records that carry it.</summary>
/// <param name="Kind">CLOBBERED / DOOR-IDX / EDGE-IDX / SPLIT / OVERLAP / CONTACT — the detector's own class name.</param>
/// <param name="A">A plugin filename as it appears in plugins.txt. Order is not significant.</param>
/// <param name="Necessary">
/// Records a fix MUST override to count as CONFIRMED. Some conflicts admit more than one legitimate
/// repair (a quest split can be fixed by moving the actor back OR by re-pointing the alias), so the
/// verifier scores against this set and reports the rest as context.
/// </param>
public sealed record Conflict(
    string Kind,
    string A,
    string B,
    string Summary,
    IReadOnlyList<ConflictRecord> Necessary,
    IReadOnlyList<ConflictRecord>? Alternative = null);

/// <summary>A base emit's record re-measured under THIS emit's arm (the probe's <c>--base</c>). Keyed the way
/// <see cref="ConflictIo.RowIdentity"/> keys a row; <paramref name="A"/>/<paramref name="B"/> are the base
/// row's attribution, carried so a reader can print it.</summary>
public sealed record Predicate(string Kind, string A, string B, string FormKey, string? Other, PhysicalState Physical, string? Detail);

/// <summary>One population a probe judged. <paramref name="Judged"/> and <paramref name="Unjudgeable"/> share the
/// unit <paramref name="Population"/> names; <paramref name="SetAside"/> lists what the class excludes by
/// definition (rule 6's counted exclusions — volume-scale, markers, non-obstructing types), so the reader sees
/// them without their counting as unjudgeable. Every number comes from a counter the probe already prints.</summary>
public sealed record CoverageLine(string Population, long Judged,
                                  IReadOnlyDictionary<string, long>? Unjudgeable = null,
                                  IReadOnlyDictionary<string, long>? SetAside = null,
                                  string? Note = null)
{
    [JsonIgnore] public long UnjudgeableTotal => Unjudgeable?.Values.Sum() ?? 0;
    /// <summary>unjudgeable / (judged + unjudgeable); 0 when nothing was in the population.</summary>
    [JsonIgnore] public double UnjudgeableFraction => Judged + UnjudgeableTotal == 0 ? 0 : (double)UnjudgeableTotal / (Judged + UnjudgeableTotal);
}

/// <param name="Coverage">What the probe judged and could not, per population (Part B of the 2026-09-12 handoff).</param>
/// <param name="Predicates">Present when the probe ran with <c>--base</c>: that emit's records under this arm.</param>
/// <param name="BaseEmit">The path <c>--base</c> named, for the record.</param>
/// <param name="Population">WHICH REFERENCES the run judged (Q27, 2026-09-06): <c>load-time</c> — the XESP enable chain,
/// the default — or <c>flags</c> — each record's own initially-disabled flag, what <c>--flags</c> asks for and what every
/// run before Q27 judged. Null in an emit written before then, and a reader must treat that as UNKNOWN: an old emit is
/// not certified equal to a new one, and crediting a repair across two different populations credits the population
/// change.</param>
/// <param name="State">WHICH WORLD STATE the run judged: <c>baseline</c> (the load-time state), a <c>--state</c> arm's
/// name, or null for a flag-population run (a flag population is not a world state) and for any emit written before Q27.
/// Two emits of different states can be compared deliberately; they cannot be credited as a repair by accident.</param>
public sealed record ConflictFile(string Probe, string Instance, string GeneratedUtc, IReadOnlyList<Conflict> Conflicts,
                                  IReadOnlyList<CoverageLine>? Coverage = null,
                                  IReadOnlyList<Predicate>? Predicates = null,
                                  string? BaseEmit = null,
                                  string? Population = null,
                                  string? State = null)
{
    /// <summary>How this emit's population and state read for a human, and for the diff's refusal message. "unknown"
    /// is a real answer: an emit written before Q27 says nothing about either, and pretending otherwise is the silent
    /// certification the credit rule exists to stop.</summary>
    public string PopulationIdentity => $"{Population ?? "unknown"} / state {State ?? "unknown"}";
}

public static class ConflictIo
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Write(string path, ConflictFile file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(file, Opts));
    }

    public static ConflictFile Read(string path)
        => JsonSerializer.Deserialize<ConflictFile>(File.ReadAllText(path), Opts)
           ?? throw new InvalidDataException($"empty or unreadable conflict file: {path}");

    /// <summary>What a row IS, for the diff: the records its predicate is about, never the plugin pair. A clobber is
    /// (the navmesh, the loser plugin) — the winner is attribution; a dangling link is its two ends; an overlap is its two
    /// refs (unordered — the later/other roles can swap under a patch); a contact row is (dependent, host). Keyed on the
    /// plugin pair, a sham that re-provides three dangling navmeshes read as three repaired and three created
    /// (2026-09-03, the navmesh S1 cell); keyed on the records it reads as three unchanged, re-attributed.</summary>
    public static string RowIdentity(string kind, string formKey, string? other, string a)
    {
        if (other is null) return $"{kind}|{formKey}|{a}";
        return string.CompareOrdinal(formKey, other) <= 0 ? $"{kind}|{formKey}|{other}" : $"{kind}|{other}|{formKey}";
    }
}
