using System.Text.RegularExpressions;
using RunControl;

namespace EvalControl;

/// <summary>`evalcontrol bundle --media FILE`: which supplied pictures and clips illustrate a measured arm (evalrun.py's
/// media vocabularies, state_media_fingerprints and media_item_refusal; moved out of runcontrol's bundle with the
/// runtime.supplied-observations row). An item is accepted only when its fingerprint is one an accepted receipt of the
/// bound app scan measured on the arm it names; every other item is listed unassessed with one closed reason.</summary>
public static class MediaRule
{
    public static readonly string[] Kinds = ["offline-render", "native-screenshot", "game-video", "historical-illustration"];
    public static readonly string[] FingerprintSources = ["emitted", "asserted"];
    public static readonly string[] Reasons =
    [
        "file-missing", "kind-missing", "subject-missing", "state-missing", "fingerprint-missing", "fingerprint-source-missing",
        "provenance-missing", "no-accepted-receipt-on-arm", "fingerprint-mismatch", "source-missing", "original-missing",
        "texture-policy-missing", "texture-policy-mixed",
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
                if (Fingerprints.Fingerprint(cfg, p, name) is string fp) found.Add(fp);
        }
        return found;
    }

    /// <summary>What the receipts of a finished app scan measured, for the media rule: each arm's own fingerprints
    /// (accepted receipts whose part is null or "arm"), what its reverse and diff parts consumed (which only ever
    /// explains a refusal), and the state fingerprints. <paramref name="outputAt"/> maps a receipt's output path to the
    /// file (in a bundle, under evidence/).</summary>
    public sealed class Measured
    {
        public Dictionary<string, HashSet<string>> Own { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> Other { get; } = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<PyDict>> _configs = new(StringComparer.Ordinal);
        readonly HashSet<string> _states = new(StringComparer.Ordinal);

        public Measured(PyDict receipts, Func<string, string> outputAt)
        {
            foreach (var (_, recRaw) in receipts)
            {
                var rec = PyValue.Dict(recRaw);
                if (!PyValue.Truthy(PyValue.Dict(rec["validation"])["accepted"])) continue;
                var cfg = Ev.OrEmpty(Ev.OrEmpty(rec.Get("consumed")).Get("configuration"));
                object? fp = cfg.Get("fingerprint");
                if (PyValue.Truthy(fp))
                {
                    bool own = rec.Get("part") is null or "arm";
                    string arm = PyValue.Hash(rec["armId"]);
                    var target = own ? Own : Other;
                    if (!target.ContainsKey(arm)) target[arm] = new HashSet<string>(StringComparer.Ordinal);
                    target[arm].Add(PyValue.Hash(fp));
                    if (own)
                    {
                        if (!_configs.ContainsKey(arm)) _configs[arm] = [];
                        _configs[arm].Add(cfg);
                    }
                }
                if (Ev.Eq(rec.Get("checkId"), "state.conditional-geometry"))
                    // the world states this run measured: every switch a state row's Kind names
                    foreach (var o in Ev.Items(rec.Get("outputs")).Select(PyValue.Dict))
                        if (PyValue.Truthy(o.Get("path")) && PyJson.LoadEvalFile(outputAt((string)o["path"]!)) is PyDict doc)
                            foreach (var c in Ev.Items(doc.Get("Conflicts")))
                                if (StateSwitch(Ev.G(c, "Kind")) is string name) _states.Add(name);
            }
        }

        public HashSet<string> State(string arm, object? state) => StateFingerprints(_configs.GetValueOrDefault(arm) ?? [], _states, state);
    }

    /// <summary>Why a media item's identity does not tie it to a measured arm: the reasons no-accepted-receipt-on-arm and
    /// fingerprint-mismatch, or (null, null). The part of the rule that reads no file, so a bundle's verify can apply
    /// it again to an item it holds.</summary>
    public static (string? reason, string? detail) IdentityRefusal(PyDict item, IReadOnlyDictionary<string, HashSet<string>> fingerprints,
        IReadOnlyDictionary<string, HashSet<string>> otherFingerprints, Func<string, object?, HashSet<string>>? stateFingerprints)
    {
        object? fp = item.Get("configurationFingerprint");
        object? aid = Ev.OrEmpty(item.Get("subject")).Get("armId");
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
        return (null, null);
    }

    /// <summary>The texture policies the renders among <paramref name="items"/> say they were drawn under (viewprobe's
    /// TexturePolicy token, which capture_acquire copies from the frame's own metadata); a render that states none adds
    /// nothing here and is refused on its own.</summary>
    public static HashSet<string> RenderPolicies(IEnumerable<object?> items) =>
        items.OfType<PyDict>().Where(i => Ev.Eq(i.Get("mediaKind"), "offline-render"))
            .Select(i => i.Get("texturePolicy")).OfType<string>().Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>Why a render cannot stand beside the others: the texture policy changes a render's texture pixels (and
    /// depth at alpha-tested edges), so a render that does not say which policy drew it (one made before renders recorded
    /// it) is not accepted as matching, and renders drawn under more than one policy are never accepted together.
    /// <paramref name="renderPolicies"/>: RenderPolicies of every item supplied with this one. Other kinds are not drawn
    /// by viewprobe and carry no policy.</summary>
    public static (string? reason, string? detail) PolicyRefusal(PyDict item, IReadOnlyCollection<string>? renderPolicies)
    {
        if (!Ev.Eq(item.Get("mediaKind"), "offline-render")) return (null, null);
        if (item.Get("texturePolicy") is not string own || own.Length == 0)
            return ("texture-policy-missing", "the render does not say which texture policy drew it (it was made before renders recorded one); render it again");
        var all = new SortedSet<string>(renderPolicies ?? [], StringComparer.Ordinal) { own };
        if (all.Count > 1)
            return ("texture-policy-mixed", $"the supplied renders were drawn under {all.Count} texture policies ({string.Join(", ", all)}); "
                + "render them again under one, so pictures from two policies are not compared as one");
        return (null, null);
    }

    /// <summary>Why a media item is unassessed: one closed reason and a plain sentence, or (null, null) when it is
    /// accepted. The checks run in the standard's order. fingerprints: each arm's own configurations, arm and
    /// fingerprint each under its PyValue.Hash key; otherFingerprints: what the arm's reverse and diff parts
    /// consumed, which only ever explains a refusal; stateFingerprints(arm key, state): StateFingerprints for that arm.</summary>
    public static (string? reason, string? detail) Refusal(PyDict item, string src, IReadOnlyDictionary<string, HashSet<string>> fingerprints,
        IReadOnlyDictionary<string, HashSet<string>> otherFingerprints, Func<string, object?, HashSet<string>>? stateFingerprints,
        IReadOnlyCollection<string>? renderPolicies = null)
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
        if (IdentityRefusal(item, fingerprints, otherFingerprints, stateFingerprints) is { reason: not null } refused) return refused;
        if (PolicyRefusal(item, renderPolicies) is { reason: not null } policy) return policy;
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
