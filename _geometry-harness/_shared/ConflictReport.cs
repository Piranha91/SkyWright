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

/// <summary>One record the fix would have to touch, and what part it plays in the conflict.</summary>
/// <param name="FormKey">Round-trips through FormKey.ToString() / FormKey.Factory.</param>
/// <param name="Role">e.g. "navmesh", "door", "quest", "alias-ref" — reporting only, never matched on.</param>
/// <param name="Label">Human context, e.g. the cell name. Never load-bearing.</param>
public sealed record ConflictRecord(string FormKey, string Role, string? Label);

/// <summary>One detected conflict between two plugins, with the records that carry it.</summary>
/// <param name="Kind">CLOBBERED / DOOR-IDX / EDGE-IDX / SPLIT — the detector's own class name.</param>
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

public sealed record ConflictFile(string Probe, string Instance, string GeneratedUtc, IReadOnlyList<Conflict> Conflicts);

public static class ConflictIo
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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
}
