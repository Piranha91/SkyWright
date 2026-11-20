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
// an overlap row is "the placed render surfaces intersect (or validated solids contain one another)"
// AND "the later plugin introduced it blind"; a contact row is
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
/// <param name="Wind">The record's render geometry includes shapes with the Tree_Anim (wind) shader flag: the shapes and
/// their vertex-alpha facts (<c>NifWind</c>). Null otherwise, and then omitted. Never load-bearing.</param>
/// <param name="Triangle">On an EDGE-IDX or EDGE-NAV row's other end: the target triangle the broken link names, part of the
/// row's identity (2026-10-24 for EDGE-IDX, 2026-11-16 for EDGE-NAV: one row per broken link); on an EDGE-NAV row's linking
/// navmesh, its source triangle. Null on every other record, and then omitted.</param>
public sealed record ConflictRecord(string FormKey, string Role, string? Label, PhysicalState? Physical = null, string? PhysicalDetail = null,
                                    string? Wind = null, int? Triangle = null);

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
    IReadOnlyList<ConflictRecord>? Alternative = null,
    MeshCause? Mesh = null);

/// <summary>On a row a mod folder's mesh alone creates (mesh-responsibility): <see cref="Conflict.B"/> is then the folder, and
/// <see cref="Conflict.A"/> the plugin that placed the other object. Its presence, never the name's shape, says B is a folder.
/// <paramref name="Swapped"/>: each reference the folder reshapes; <paramref name="PlacedBy"/>: the plugins that set the two
/// placements (the Necessary reference's first); <paramref name="AlsoFolders"/>: other folders' meshes on the pair, which a
/// joint swap needed.</summary>
public sealed record MeshCause(string Folder, IReadOnlyList<MeshSwap> Swapped, IReadOnlyList<string> PlacedBy, string Sight,
                               string Why, bool Joint = false, IReadOnlyList<string>? AlsoFolders = null);

/// <summary>One reshaped reference: its model path, the folder's copy's sha256, and the copy that wins without the folder
/// (its MO2 layer, the archive it is in if any, and its sha256).</summary>
public sealed record MeshSwap(string Reference, string Model, string Sha256, string ReplacesFolder, string? ReplacesArchive, string ReplacesSha256);

/// <summary>A base emit's record re-measured under THIS emit's arm (the probe's <c>--base</c>). Keyed the way
/// <see cref="ConflictIo.RowIdentity"/> keys a row; <paramref name="A"/>/<paramref name="B"/> are the base
/// row's attribution, carried so a reader can print it. <paramref name="OtherTriangle"/> is the base row's
/// <see cref="ConflictRecord.Triangle"/> (an EDGE-IDX link's target triangle); null, and omitted, otherwise.</summary>
public sealed record Predicate(string Kind, string A, string B, string FormKey, string? Other, PhysicalState Physical, string? Detail,
                               int? OtherTriangle = null);

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
/// <param name="Configuration">WHAT THE RUN CONSUMED (roadmap 4.20, 2026-09-11): the loaded plugins in order with their content
/// hashes, the asset providers in priority, the runtime overlays, and the limits of the view — <see cref="HarnessOrderCli"/>
/// builds it, keyed with this emit's own Population and State. Null in an emit written before 4.20, and a reader must treat
/// that as UNKNOWN rather than "the same": every verdict in this file is true of ONE configuration, and comparing two emits
/// whose configurations are not both named compares two unnamed things. <see cref="ConfigurationGate"/> is that rule.</param>
/// <param name="PairLimits">The per-pair work and time limits the probe measured under (overlapprobe, 2026-10-19); null in an emit
/// of another probe or written before then. overlapprobe --reverse-of carries a baseline's rows only under the same limits.</param>
public sealed record ConflictFile(string Probe, string Instance, string GeneratedUtc, IReadOnlyList<Conflict> Conflicts,
                                  IReadOnlyList<CoverageLine>? Coverage = null,
                                  IReadOnlyList<Predicate>? Predicates = null,
                                  string? BaseEmit = null,
                                  string? Population = null,
                                  string? State = null,
                                  ConfigurationIdentity? Configuration = null,
                                  string? MeasurementPolicy = null,
                                  PairLimits? PairLimits = null)
{
    /// <summary>How this emit's population and state read for a human, and for the diff's refusal message. "unknown"
    /// is a real answer: an emit written before Q27 says nothing about either, and pretending otherwise is the silent
    /// certification the credit rule exists to stop.</summary>
    public string PopulationIdentity => $"{Population ?? "unknown"} / state {State ?? "unknown"}";
}

/// <summary>The per-pair limits an actual-shape query ran under: the triangle-pair work cap and the seconds per pair.</summary>
public sealed record PairLimits(long MaxTrianglePairs, double PairSeconds);

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
    /// <para>CASE-INSENSITIVE (2026-09-11). A plugin filename's letter case can differ between loadorder.txt, a mod folder
    /// and the game Data folder, and the FormKey text carries that filename. On the lab, one navownprobe row read
    /// `ccBGSSSE025-AdvDSGS.esm` from one provider and `ccbgssse025-advdsgs.esm` from another, split into GONE + NEW,
    /// and turned a no-op what-if into DIFFERENTIAL-RECLASSIFIED. The engine and MO2 treat the names as one file;
    /// so does the key.</para>
    /// <para>A PART (2026-10-24): an EDGE-IDX row is one broken link, so its identity is the linking navmesh, the target in
    /// their roles (direction kept) and the target triangle; without a part the string is exactly what it was.</para>
    public static string RowIdentity(string kind, string formKey, string? other, string a, int? part = null)
    {
        kind = kind.ToLowerInvariant(); formKey = formKey.ToLowerInvariant(); a = a.ToLowerInvariant(); other = other?.ToLowerInvariant();
        if (other is null) return $"{kind}|{formKey}|{a}";
        if (part is not null) return $"{kind}|{formKey}|{other}|{part}";
        return string.CompareOrdinal(formKey, other) <= 0 ? $"{kind}|{formKey}|{other}" : $"{kind}|{other}|{formKey}";
    }
}

/// <summary>WHICH IDENTITY an emit's EDGE-IDX rows were written under (2026-10-24): <c>true</c> when every EDGE-IDX row names its
/// target triangle, <c>false</c> when one does not (an emit written before then keys the links of one navmesh pair as one
/// row), <c>null</c> when the emit has no EDGE-IDX row. Read from the data, never from the probe's version.</summary>
public static class EdgeLinkIdentity
{
    public static bool? PerLink(ConflictFile f)
    {
        var edge = f.Conflicts.Where(c => c.Kind == "EDGE-IDX").ToList();
        if (edge.Count == 0) return null;
        return edge.All(c => c.Alternative is { Count: > 0 } alt && alt[0].Triangle is not null);
    }
}

/// <summary>What the gate decided. <paramref name="Refused"/> means the comparison does not run (exit 3);
/// <paramref name="Lines"/> is what to print either way, and <paramref name="Delta"/> is the configuration
/// difference the differential is OF — empty when the two runs consumed the same configuration.</summary>
public sealed record ConfigurationGateResult(bool Refused, IReadOnlyList<string> Lines, IReadOnlyList<string> Delta);

/// <summary>THE CONFIGURATION GATE for a differential (roadmap 4.20, 2026-09-11) — the same posture as the Q27
/// population/state gate one file over in conflictdiff, applied to what the two runs CONSUMED.
///
/// <para>A configuration difference is never refused when it is named: the delta between two configurations is
/// exactly what a differential is a differential OF, so it is printed and the run proceeds. What IS refused is a
/// comparison that cannot state what it is comparing — an emit with no configuration at all (an unlabelled emit is
/// UNKNOWN, not "the same") and two emits from different probes (their rows are not the same kind of thing).</para>
///
/// <para>And the case that has no honest verdict: identical configurations that produced different rows. Nothing
/// the identity covers changed, so the difference came from somewhere the identity does not model or from the run
/// itself — either way it is not a repair, and it is not something to average over. That is DIFFERENTIAL-UNJUDGED
/// with the reason said out loud.</para>
///
/// <para>WHY THIS LIVES IN THE FORMAT FILE and not in DetectorModel, where <c>Credit</c> and
/// <c>DifferentialVerdict</c> live: those decide what a ROW is worth — detector semantics, and DetectorModel is
/// linked by eighteen projects that know nothing of <see cref="ConflictFile"/>. This decides whether a FILE says
/// enough about itself to be compared at all, it reads only <see cref="ConflictFile"/>'s own fields, and putting it
/// here keeps <see cref="ConfigurationIdentity"/> off those eighteen projects' compile lists. <c>RowIdentity</c>
/// above is the same shape of thing: a diff-side rule about the format, kept with the format.</para></summary>
public static class ConfigurationGate
{
    public static ConfigurationGateResult Check(ConflictFile baseFile, ConflictFile changedFile)
    {
        var lines = new List<string>();

        if (!string.Equals(baseFile.Probe, changedFile.Probe, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"the two emits are from DIFFERENT probes ('{baseFile.Probe}' and '{changedFile.Probe}'): their rows are");
            lines.Add("not the same kind of thing, so nothing crosses between them — a row present in one and absent in the other");
            lines.Add("means only that the two detectors detect different things. REFUSED.");
            return new ConfigurationGateResult(true, lines, Array.Empty<string>());
        }

        // A changed geometry policy is a changed question, even when the files and world state
        // are identical. Historical overlap emits lack this identity and cannot establish repair
        // against current actual-shape measurements. Keep them readable for explicitly labelled audits.
        if (string.Equals(baseFile.Probe, "overlapprobe", StringComparison.OrdinalIgnoreCase)
            || baseFile.MeasurementPolicy is not null || changedFile.MeasurementPolicy is not null)
        {
            if (string.IsNullOrWhiteSpace(baseFile.MeasurementPolicy)
                || string.IsNullOrWhiteSpace(changedFile.MeasurementPolicy)
                || !string.Equals(baseFile.MeasurementPolicy, changedFile.MeasurementPolicy, StringComparison.Ordinal))
            {
                lines.Add($"measurement policy: base '{baseFile.MeasurementPolicy ?? "unknown"}', changed '{changedFile.MeasurementPolicy ?? "unknown"}'.");
                lines.Add("Unknown or different measurement policies cannot establish repair credit. Re-emit both arms with the same policy. REFUSED.");
                return new ConfigurationGateResult(true, lines, Array.Empty<string>());
            }
            lines.Add($"measurement policy: {baseFile.MeasurementPolicy}");
        }

        var a = baseFile.Configuration;
        var b = changedFile.Configuration;
        if (a is null || b is null)
        {
            string which = a is null && b is null ? "neither emit says" : a is null ? "the base emit does not say" : "the changed emit does not say";
            lines.Add($"{which} which configuration produced it (written before 2026-09-11, or by a probe that was not wired).");
            lines.Add("An unlabelled emit is UNKNOWN, not \"the same\": every verdict is true of one configuration — one set of plugins in");
            lines.Add("one order over one asset view — and a row that vanished between two configurations nobody named is not evidence of");
            lines.Add("anything. Re-emit with the current probe. REFUSED.");
            return new ConfigurationGateResult(true, lines, Array.Empty<string>());
        }

        lines.Add($"configuration (base)   : {a.Short}");
        lines.Add($"configuration (changed): {b.Short}");
        var delta = a.DeltaTo(b);
        if (delta.Count == 0)
            lines.Add("configuration delta    : none — the two runs consumed the same plugins, in the same order, over the same providers and overlays.");
        else
        {
            lines.Add("configuration delta    : this is what the differential below is a difference OF —");
            foreach (var d in delta) lines.Add($"   - {d}");
        }
        return new ConfigurationGateResult(false, lines, delta);
    }

    /// <summary>The rows differ but the configuration does not. Nothing the identity covers changed, so the rows changed
    /// for a reason this file cannot name — a non-deterministic detector, or an input the identity does not model. Never
    /// a repair verdict.</summary>
    public static bool Nondeterministic(ConfigurationGateResult gate, bool anyRowDiffers)
        => !gate.Refused && gate.Delta.Count == 0 && anyRowDiffers;

    public const string NondeterminismLine =
        "!! IDENTICAL CONFIGURATIONS PRODUCED DIFFERENT ROWS. The two emits consumed the same plugins in the same order over the "
      + "same asset providers and overlays, and still disagree. That is non-determinism in the detector or an input the "
      + "configuration identity does not model — it is NOT a repair, and no number below is credit.";
}
