using System.Text.RegularExpressions;
using HarnessShared;

namespace RunControl;

/// <summary>`bundle --media FILE`: which supplied pictures and clips illustrate a measured arm (evalrun.py's media
/// vocabularies, state_media_fingerprints and media_item_refusal). An item is accepted only when its fingerprint is
/// one an accepted receipt measured on the arm it names; every other item is listed unassessed with one closed reason.</summary>
public static class BundleMedia
{
    public static readonly string[] Kinds = ["offline-render", "native-screenshot", "game-video", "historical-illustration"];
    public static readonly string[] FingerprintSources = ["emitted", "asserted"];
    public static readonly string[] Reasons =
    [
        "file-missing", "kind-missing", "subject-missing", "state-missing", "fingerprint-missing", "fingerprint-source-missing",
        "provenance-missing", "no-accepted-receipt-on-arm", "fingerprint-mismatch", "source-missing", "original-missing",
    ];

    static readonly Regex ScopedPopulation = new(@"\+population:[0-9a-f]{12}$", RegexOptions.CultureInvariant);

    /// <summary>The switch a state row's Kind names (STATE-X@LLLLLL:Plugin@...), or null.</summary>
    public static string? StateSwitch(object? kind)
    {
        var m = Regex.Match(PyValue.Truthy(kind) ? Ev.Str(kind) : "", @"^STATE-[A-Z]+@([0-9A-Fa-f]{6}:[^@]+)@", RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : null;
    }

    // (v or "") as text.
    static string Text(object? v) => PyValue.Truthy(v) ? Ev.Str(v) : "";

    /// <summary>The configuration fingerprint of a recorded configuration with another population and state, by the
    /// owner's rule (ConfigurationIdentity.Compute); null for a configuration schema the owner does not compute.</summary>
    public static string? Fingerprint(PyDict cfg, string population, string state)
    {
        if (cfg.Get("schemaVersion") is { } schema && !Ev.Eq(schema, (long)ConfigurationIdentity.CurrentSchema)) return null;
        var plugins = Ev.Items(cfg.Get("plugins")).Select(p => new ConfigPlugin(Text(Ev.G(p, "name")), "", Text(Ev.G(p, "sha256")), 0, "", false, false, [])).ToList();
        var providers = Ev.Items(cfg.Get("providers")).Select(v => new ConfigProvider(Text(Ev.G(v, "name")), Text(Ev.G(v, "kind")))).ToList();
        var overlays = Ev.Items(cfg.Get("overlays")).Select(o => new ConfigOverlay(Text(Ev.G(o, "kind")), "", Text(Ev.G(o, "sha256")))).ToList();
        return ConfigurationIdentity.Compute("", plugins, providers, overlays, population, state).Fingerprint;
    }

    /// <summary>`runcontrol fingerprint`: the fingerprint of each configuration in a JSON list, by the owner's rule, for
    /// the Python that stays and must not hold its own copy of it. A configuration names its population and state as
    /// text; a schemaVersion other than the owner's, or none of these shapes, is refused in one sentence.</summary>
    public static List<object?> Fingerprints(object? doc)
    {
        if (doc is not List<object?> list)
            throw new Refusal("fingerprint reads a JSON list of configurations on standard input; give it a list, even of one.");
        var found = new List<object?>();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not PyDict cfg)
                throw new Refusal($"fingerprint: configuration {i} is not a JSON object; give plugins, providers, overlays, population and state.");
            if (cfg.Get("population") is not string population || cfg.Get("state") is not string state)
                throw new Refusal($"fingerprint: configuration {i} names no population or state as text; give both, as the run recorded them.");
            found.Add(Fingerprint(cfg, population, state) ?? throw new Refusal(
                $"fingerprint: configuration {i} has schemaVersion {PyText.Repr(cfg.Get("schemaVersion"))}, and the owner computes only "
                + $"schema {ConfigurationIdentity.CurrentSchema}; it was written by another build, so measure it again with this one."));
        }
        return found;
    }

    /// <summary>The configurations a picture rendered in a world state may carry: the arm's own accepted
    /// configurations, with the population as recorded and as the renderer reads it (a scoped population's base),
    /// and only the state line set to that state; none for a state this run's state checks did not measure.</summary>
    public static HashSet<string> StateFingerprints(IEnumerable<PyDict> ownConfigs, IReadOnlySet<string> measuredStates, object? state)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (state is not string name || !measuredStates.Contains(name)) return found;
        foreach (var cfg in ownConfigs)
        {
            string pop = Text(cfg.Get("population"));
            foreach (string p in new[] { pop, ScopedPopulation.Replace(pop, "") })
                if (Fingerprint(cfg, p, name) is string fp) found.Add(fp);
        }
        return found;
    }

    /// <summary>Why a media item is unassessed: one closed reason and a plain sentence, or (null, null) when it is
    /// accepted. The checks run in the standard's order. fingerprints: each arm's own configurations, arm and
    /// fingerprint each under its PyValue.Hash key; otherFingerprints: what the arm's reverse and diff parts
    /// consumed, which only ever explains a refusal; stateFingerprints(arm key, state): StateFingerprints for that arm.</summary>
    public static (string? reason, string? detail) Refusal(PyDict item, string src, IReadOnlyDictionary<string, HashSet<string>> fingerprints,
        IReadOnlyDictionary<string, HashSet<string>> otherFingerprints, Func<string, object?, HashSet<string>>? stateFingerprints)
    {
        static bool Missing(string path) => !Ev.Exists(path) || Directory.Exists(path);
        if (!PyValue.Truthy(item.Get("path")) || Missing(src))
            return ("file-missing", $"the file {PyText.Repr(item.Get("path"))} does not exist");
        object? kind = item.Get("mediaKind");
        if (kind is not string k || !Kinds.Contains(k))
            return ("kind-missing", kind == null ? "mediaKind is absent (a schema-1 item)" : $"mediaKind {PyText.Repr(kind)} is not one of {string.Join(", ", Kinds)}");
        if (item.Get("subject") is not PyDict subject || !new[] { "checkId", "armId", "description" }.All(x => PyValue.Truthy(subject.Get(x))))
            return ("subject-missing", "subject must carry checkId, armId and description");
        if (!PyValue.Truthy(item.Get("state"))) return ("state-missing", "the item names no world state");
        object? fp = item.Get("configurationFingerprint");
        if (!PyValue.Truthy(fp))
            return ("fingerprint-missing", "the item names no configuration fingerprint: it cannot be tied to a measured arm");
        object? fps = item.Get("fingerprintSource");
        if (fps is not string f || !FingerprintSources.Contains(f))
            return ("fingerprint-source-missing", fps == null ? "fingerprintSource is absent (a schema-1 item)"
                : $"fingerprintSource {PyText.Repr(fps)} is not one of {string.Join(", ", FingerprintSources)}");
        if (item.Get("provenance") is not PyDict prov || !PyValue.Truthy(prov.Get("tool")) || !PyValue.Truthy(prov.Get("capturedUtc")))
            return ("provenance-missing", "provenance must be an object naming tool and capturedUtc");
        object? aid = subject["armId"];
        string arm = PyValue.Hash(aid), print = PyValue.Hash(fp);
        if (!fingerprints.ContainsKey(arm))
            return ("no-accepted-receipt-on-arm", $"no accepted receipt measured arm {PyText.Repr(aid)}'s own configuration"
                + (otherFingerprints.ContainsKey(arm) ? " (its reverse / diff parts measured the other order)" : ""));
        bool inState = item.Get("state") is not (null or "baseline") && stateFingerprints != null
            && fp is string fpText && stateFingerprints(arm, item["state"]).Contains(fpText);
        if (!fingerprints[arm].Contains(print) && !inState)
        {
            if (otherFingerprints.TryGetValue(arm, out var other) && other.Contains(print))
                return ("fingerprint-mismatch", $"the fingerprint {Ev.Head(fp, 12)} belongs to the other order this arm's reverse part "
                    + $"measured, not to arm {Ev.Str(aid)}'s own configuration; a picture of the baseline order illustrates the baseline arm");
            return ("fingerprint-mismatch", $"the fingerprint {Ev.Head(fp, 12)} does not match any accepted receipt's configuration on "
                + $"arm {Ev.Str(aid)}: this is not an illustration of a verified result");
        }
        if (item.Get("sourceInterval") is { } interval)
        {
            if (interval is not PyDict iv || !PyValue.Truthy(iv.Get("sourcePath"))) return ("source-missing", "sourceInterval names no sourcePath");
            string sp = PyPath.Str((string)iv["sourcePath"]!);
            if (Missing(sp)) return ("source-missing", $"the clip's source {PyText.Repr(iv["sourcePath"])} does not exist");
            if (!PyValue.Truthy(iv.Get("sourceSha256"))) return ("source-missing", "sourceInterval names no sourceSha256 to check the source against");
            string? got = Hashing.Sha256FileOrNull(sp);
            if (!Ev.Eq(got, iv["sourceSha256"]))
                return ("source-missing", $"the clip's source hashes to {Ev.Head(got, 12)}, not the recorded {Ev.Head(iv["sourceSha256"], 12)}");
        }
        if (item.Get("original") is { } original)
        {
            if (original is not PyDict o || !PyValue.Truthy(o.Get("path"))) return ("original-missing", "original names no path");
            string op = PyPath.Str((string)o["path"]!);
            if (Missing(op)) return ("original-missing", $"the original {PyText.Repr(o["path"])} does not exist");
            if (!PyValue.Truthy(o.Get("sha256"))) return ("original-missing", "original names no sha256 to check the file against");
            string? got = Hashing.Sha256FileOrNull(op);
            if (!Ev.Eq(got, o["sha256"]))
                return ("original-missing", $"the original hashes to {Ev.Head(got, 12)}, not the recorded {Ev.Head(o["sha256"], 12)}");
        }
        return (null, null);
    }
}
