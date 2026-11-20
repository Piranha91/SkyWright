using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarnessShared;

// THE CONFIGURATION IDENTITY AND THE CANDIDATE FILE (roadmap 4.20, 2026-09-11).
//
// A compatibility verdict is only true of the configuration it was computed from: which plugins loaded,
// in what order, with what content; which mod folders provided assets and in what priority; which runtime
// overlays were read; which population and world state the run judged. Until now an emit named its probe,
// its instance and its population/state and nothing else, so two emits of different orders were
// indistinguishable by their own contents, and a cache keyed on "the same winners" would have reused a
// history-dependent detector's answer across two different histories (an overlap row is Introduced or
// Inherited by WHO CAME BEFORE, not by who won).
//
// Two things live here, and only the shapes:
//
//   ConfigurationIdentity  what a run CONSUMED — written into every emit (ConflictFile.Configuration) and
//                          into the cache key of anything that reuses a result. Its Fingerprint covers
//                          content + order + asset providers + overlays + population + state. There is
//                          deliberately NO winners-only fingerprint: a caller who wants one is asking to
//                          reuse a history-dependent answer across histories, and the proof cases pin that
//                          two orders with equal winners get different fingerprints.
//   CandidateFile          what a run is ASKED to consume — the input a probe takes with --order FILE:
//                          the loaded plugins in load order (names) and, optionally, the enabled mod
//                          folders in asset priority. Plugin order and asset priority are separate inputs
//                          on purpose (the MO2 model): a candidate that changes plugin order and says
//                          nothing about mods keeps the base profile's asset view, and the identity says so.
//
// Resolution (names -> paths, legality, the asset view that follows the candidate) is HarnessOrderCli.cs;
// the ordering rules and the bounded search are OrderRules.cs / OrderSearch.cs. This file compiles against
// nothing but the BCL so every probe and proofprobe can link it.

/// <summary>The TES4 header facts an ordering rule needs: the declared masters, and the two flags that decide
/// which group a plugin loads in. <paramref name="MasterFlag"/> is the ESM bit (0x1) OR an <c>.esm</c>/<c>.esl</c>
/// extension — the engine treats those extensions as master-flagged whatever the header says; <paramref name="LightFlag"/>
/// is the ESL bit (0x200). Null facts (unreadable file) make no claim, never "assume no masters".</summary>
public sealed record PluginHeader(IReadOnlyList<string> Masters, bool MasterFlag, bool LightFlag)
{
    const uint EsmBit = 0x1, EslBit = 0x200;

    /// <summary>The record count the header states (HEDR), or null when it states none.</summary>
    public uint? Records { get; init; }

    /// <summary>The header's flags word as the file holds it. <see cref="MasterFlag"/> and <see cref="LightFlag"/> also count the
    /// extension; these two are the header bits alone, for a caller that records what the file states.</summary>
    public uint Flags { get; init; }
    public bool EsmBitSet => (Flags & EsmBit) != 0;
    public bool EslBitSet => (Flags & EslBit) != 0;

    /// <summary>An <c>.esm</c> or <c>.esl</c> file name: the engine loads such a file with the masters whatever its header says.</summary>
    public static bool MasterExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Equals(".esm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".esl", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Read the header off disk. Header-only, dependency-free (the same read SyntheticOrder.TryReadMasters
    /// makes, plus the flags word at offset 8). Returns null when the file is not a readable TES4 plugin.</summary>
    public static PluginHeader? TryRead(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> hdr = stackalloc byte[24];
            if (f.Read(hdr) != 24 || hdr[0] != (byte)'T' || hdr[1] != (byte)'E' || hdr[2] != (byte)'S' || hdr[3] != (byte)'4')
                return null;
            int size = BitConverter.ToInt32(hdr.Slice(4, 4));
            if (size < 0 || size > 1 << 24) return null;
            uint flags = BitConverter.ToUInt32(hdr.Slice(8, 4));
            var body = new byte[size];
            if (f.Read(body, 0, size) != size) return null;

            var masters = new List<string>();
            uint? records = null;
            int i = 0;
            while (i + 6 <= body.Length)
            {
                ushort len = BitConverter.ToUInt16(body, i + 4);
                if (i + 6 + len > body.Length) break;
                if (body[i] == (byte)'M' && body[i + 1] == (byte)'A' && body[i + 2] == (byte)'S' && body[i + 3] == (byte)'T')
                {
                    int end = i + 6 + len, stop = end;
                    while (stop > i + 6 && body[stop - 1] == 0) stop--;
                    masters.Add(Encoding.Latin1.GetString(body, i + 6, stop - (i + 6)));
                }
                else if (len >= 8 && body[i] == (byte)'H' && body[i + 1] == (byte)'E' && body[i + 2] == (byte)'D' && body[i + 3] == (byte)'R')
                    records = BitConverter.ToUInt32(body, i + 6 + 4);
                i += 6 + len;
            }
            var ext = Path.GetExtension(path);
            bool extMaster = MasterExtension(path);
            return new PluginHeader(masters, (flags & EsmBit) != 0 || extMaster, (flags & EslBit) != 0 || ext.Equals(".esl", StringComparison.OrdinalIgnoreCase)) { Records = records, Flags = flags };
        }
        catch { return null; }
    }
}

/// <summary>One loaded plugin as a run consumed it: the filename, the real path the VFS resolved it to, its content
/// hash, and its header facts. <paramref name="Provider"/> names the layer the path came from ("overwrite", a mod folder
/// name, "Data") so a reader can see WHICH copy of a duplicated filename was read.</summary>
public sealed record ConfigPlugin(string Name, string Path, string Sha256, long Bytes, string Provider,
                                  bool MasterFlag, bool LightFlag, IReadOnlyList<string> Masters);

/// <summary>One asset provider in the view: a mod folder in MO2 priority order (top = highest, first in the list), or the
/// overwrite layer, or the game Data folder. Only providers actually IN the view are listed — a disabled folder is not a
/// provider, and a plugin loaded from a disabled folder is reported in <see cref="ConfigurationIdentity.Limits"/>.</summary>
public sealed record ConfigProvider(string Name, string Kind);   // Kind: "overwrite" | "mod" | "data"

/// <summary>A runtime overlay the run read: today the Base Object Swapper ini files (<c>Kind</c> "bos-ini"), by path and hash.
/// A run that did not scan a layer says so in <see cref="ConfigurationIdentity.Unknown"/> rather than listing nothing.</summary>
public sealed record ConfigOverlay(string Kind, string Path, string Sha256);

/// <summary>What a run consumed. Written into every emit; the key of any reuse.</summary>
/// <param name="Fingerprint">sha256 (12 hex shown, full kept) over the canonical form of Plugins (name + content hash, in order),
/// Providers (in priority), Overlays, Population, State and SchemaVersion. Order-sensitive by construction.</param>
/// <param name="Source">Where the configuration came from: <c>profile:&lt;name&gt;</c>, <c>synthetic:&lt;profile&gt;</c> (a
/// <c>--enable/--disable</c> what-if) or <c>candidate:&lt;file name&gt;</c>.</param>
/// <param name="Population">"load-time" or "flags" (EnablePopulation.Identity).</param>
/// <param name="State">"baseline", a state arm's name, or "-" for the flag population.</param>
/// <param name="Limits">Things the identity knows it is NOT modelling for THIS run and that change what the consumers see — a
/// plugin whose folder is disabled (assets absent from the view), a candidate that reordered plugins but kept the base
/// profile's asset priority. Read these before believing a geometry or script result.</param>
/// <param name="Unknown">Inputs this identity does not capture at all, each with why: installer choices, the game build,
/// distributors other than BOS. Explicit, so a missing input is never read as "the same".</param>
public sealed record ConfigurationIdentity(
    int SchemaVersion,
    string Fingerprint,
    string Source,
    IReadOnlyList<ConfigPlugin> Plugins,
    IReadOnlyList<ConfigProvider> Providers,
    IReadOnlyList<ConfigOverlay> Overlays,
    string Population,
    string State,
    IReadOnlyList<string> Limits,
    IReadOnlyDictionary<string, string> Unknown)
{
    public const int CurrentSchema = 1;

    /// <summary>The unknowns every identity carries until a reader is built for the input. Spelled once, here, so no probe
    /// invents its own wording and a consumer can grep for the key.</summary>
    public static IReadOnlyDictionary<string, string> DefaultUnknown => new Dictionary<string, string>
    {
        ["installerChoices"] = "not modelled: MO2 keeps no per-mod record of the FOMOD options taken; the installed files are the evidence",
        ["gameBuild"] = "not read: the executable version and SKSE/plugin set are not part of a records run",
        ["distributors"] = "not read: SPID / KID / SkyPatcher layers are not scanned; only BOS ini files are listed as overlays",
        ["saveState"] = "not applicable: a records run judges a fresh load, never a save",
    };

    /// <summary>Compute the identity. The fingerprint is the sha256 of a canonical text: one line per input, in a fixed order,
    /// so two runs that consumed the same things produce the same hash on any machine and any path spelling (paths are NOT
    /// in the hash; names and content hashes are).</summary>
    public static ConfigurationIdentity Compute(string source, IReadOnlyList<ConfigPlugin> plugins, IReadOnlyList<ConfigProvider> providers,
                                                IReadOnlyList<ConfigOverlay> overlays, string population, string state,
                                                IReadOnlyList<string>? limits = null, IReadOnlyDictionary<string, string>? unknown = null)
    {
        var sb = new StringBuilder();
        sb.Append("schema:").Append(CurrentSchema).Append('\n');
        sb.Append("population:").Append(population).Append('\n');
        sb.Append("state:").Append(state).Append('\n');
        foreach (var p in plugins) sb.Append("plugin:").Append(p.Name.ToLowerInvariant()).Append(':').Append(p.Sha256).Append('\n');
        foreach (var v in providers) sb.Append("provider:").Append(v.Kind).Append(':').Append(v.Name.ToLowerInvariant()).Append('\n');
        foreach (var o in overlays) sb.Append("overlay:").Append(o.Kind).Append(':').Append(o.Sha256).Append('\n');
        var fp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        return new ConfigurationIdentity(CurrentSchema, fp, source, plugins, providers, overlays, population, state,
                                         limits ?? Array.Empty<string>(), unknown ?? DefaultUnknown);
    }

    /// <summary>The short form for a log line.</summary>
    public string Short => $"{Fingerprint[..12]} ({Plugins.Count} plugins, {Providers.Count} providers, {Population}/{State}, {Source})";

    /// <summary>What differs between two identities, in words a reader can act on — the delta a differential is OF. Empty
    /// when nothing the fingerprint covers differs (and then two different result sets are a defect, not a finding).</summary>
    public IReadOnlyList<string> DeltaTo(ConfigurationIdentity other)
    {
        var d = new List<string>();
        if (Population != other.Population) d.Add($"population {Population} -> {other.Population}");
        if (State != other.State) d.Add($"state {State} -> {other.State}");
        var a = Plugins.Select((p, i) => (p, i)).ToDictionary(x => x.p.Name, x => x, StringComparer.OrdinalIgnoreCase);
        var b = other.Plugins.Select((p, i) => (p, i)).ToDictionary(x => x.p.Name, x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, x) in a) if (!b.ContainsKey(name)) d.Add($"plugin removed: {name} (was at {x.i})");
        foreach (var (name, x) in b) if (!a.ContainsKey(name)) d.Add($"plugin added: {name} (at {x.i})");
        foreach (var (name, x) in a)
        {
            if (!b.TryGetValue(name, out var y)) continue;
            if (!string.Equals(x.p.Sha256, y.p.Sha256, StringComparison.OrdinalIgnoreCase)) d.Add($"plugin content changed: {name}");
        }
        // relative order: a pair present on both sides whose order flipped
        var commonA = Plugins.Where(p => b.ContainsKey(p.Name)).Select(p => p.Name).ToList();
        var commonB = other.Plugins.Where(p => a.ContainsKey(p.Name)).Select(p => p.Name).ToList();
        var posB = commonB.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
        int flips = 0; string? firstFlip = null;
        for (int i = 0; i < commonA.Count; i++)
            for (int j = i + 1; j < commonA.Count; j++)
                if (posB[commonA[i]] > posB[commonA[j]]) { flips++; firstFlip ??= $"{commonA[i]} / {commonA[j]}"; }
        if (flips > 0) d.Add($"plugin order changed: {flips} pair(s) flipped, first {firstFlip}");
        var pa = Providers.Select(p => $"{p.Kind}:{p.Name}").ToList();
        var pb = other.Providers.Select(p => $"{p.Kind}:{p.Name}").ToList();
        if (!pa.SequenceEqual(pb, StringComparer.OrdinalIgnoreCase))
        {
            var only1 = pa.Except(pb, StringComparer.OrdinalIgnoreCase).ToList();
            var only2 = pb.Except(pa, StringComparer.OrdinalIgnoreCase).ToList();
            d.Add($"asset providers changed: {(only1.Count > 0 ? $"removed {string.Join(", ", only1)}; " : "")}{(only2.Count > 0 ? $"added {string.Join(", ", only2)}; " : "")}{(only1.Count == 0 && only2.Count == 0 ? "priority reordered" : "")}".TrimEnd(' ', ';'));
        }
        var oa = Overlays.Select(o => $"{o.Kind}:{o.Sha256}").OrderBy(s => s).ToList();
        var ob = other.Overlays.Select(o => $"{o.Kind}:{o.Sha256}").OrderBy(s => s).ToList();
        if (!oa.SequenceEqual(ob)) d.Add($"overlays changed: {oa.Count} -> {ob.Count} file(s) or content differs");
        return d;
    }

    public static string Sha256File(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }
}

/// <summary>A candidate configuration a probe is asked to evaluate (<c>--order FILE</c>). Names, never paths: the probe resolves
/// them through the same VFS map the real order uses, and REFUSES a candidate that names a plugin nothing provides, that loads a
/// plugin before its master, or that puts a non-master plugin ahead of a master-flagged one (the engine would not load that
/// order as written, so a finding computed from it would describe an impossible game).</summary>
/// <param name="Plugins">The LOADED plugins, load order, winner last. A plugin in the base profile's loadorder.txt that is absent
/// here is not loaded. Names as they appear in loadorder.txt.</param>
/// <param name="Mods">Enabled mod folders, asset priority, top = highest (the modlist.txt sense). Null keeps the base profile's
/// enabled mods and priority — and the identity then records that the asset view is the profile's, not the candidate's.</param>
/// <param name="Changes">What this candidate changes against its base, in words ("moved X above Y"), for the report. Never
/// load-bearing.</param>
/// <param name="Provenance">Why this candidate exists: the rule(s) it satisfies or tests, the search step that produced it.
/// Any JSON value, read as data and never load-bearing here; evalreport reads <c>patches</c> / <c>patchPlugins</c> as a list.</param>
public sealed record CandidateFile(
    int SchemaVersion,
    string Name,
    string BaseProfile,
    IReadOnlyList<string> Plugins,
    IReadOnlyList<string>? Mods,
    IReadOnlyList<string> Changes,
    IReadOnlyDictionary<string, JsonElement>? Provenance)
{
    /// <summary>Provenance whose values are all text, as orderprobe writes it; the file's bytes are those of the text.</summary>
    public static IReadOnlyDictionary<string, JsonElement> TextProvenance(IReadOnlyDictionary<string, string> values)
        => values.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static CandidateFile Read(string path)
    {
        var c = JsonSerializer.Deserialize<CandidateFile>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"empty or unreadable candidate file: {path}");
        if (c.SchemaVersion != ConfigurationIdentity.CurrentSchema)
            throw new InvalidDataException($"{path}: candidate schema {c.SchemaVersion} is not {ConfigurationIdentity.CurrentSchema}; refused rather than guessed at");
        if (c.Plugins is null || c.Plugins.Count == 0) throw new InvalidDataException($"{path}: a candidate with no plugins loads nothing; refused");
        return c;
    }

    public void Write(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
