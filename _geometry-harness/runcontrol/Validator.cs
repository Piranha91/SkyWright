using System.Numerics;
using System.Text.RegularExpressions;
using HarnessShared;

namespace RunControl;

/// <summary>Every rejection cause of a receipt is decided here, from evidence on disk (evalrun.py's Validator).</summary>
public sealed class Validator
{
    public string Run { get; }
    public PyDict Plan { get; }

    // (reference arm, population, state) -> fingerprint: two probes of one arm that judge different populations
    // state different fingerprints over the same inputs, so the key carries both.
    readonly Dictionary<(string, string, string), object?> _armFingerprints = [];
    readonly Dictionary<string, Dictionary<string, object?>> _armPluginHashes = new(StringComparer.Ordinal);
    readonly HashSet<string> _seenKeys = new(StringComparer.Ordinal);

    /// <summary>A caller's result kinds whose rows are evidence, not defects, as a catalogue's are (another
    /// program's list); runcontrol's own validator has none, and the set only adds to `catalogue`.</summary>
    public IReadOnlySet<string> MoreCatalogueKinds { get; set; } = new HashSet<string>();

    public Validator(string run, PyDict plan)
    {
        Run = PyPath.Str(run);
        Plan = plan;
    }

    /// <summary>A refusal marker at the start of a log line: REFUSED, MISSING MASTER, CANNOT ENABLE, PROFILE MISMATCH.</summary>
    public static readonly Regex RefusedRe = PyRegex.Compile(@"^.{0,40}?(REFUSED|MISSING MASTER|CANNOT ENABLE|PROFILE MISMATCH)", multiline: true);

    /// <summary>Kinds that read a load order, and so can refuse the configuration itself.</summary>
    public static readonly HashSet<string> OrderConsumingKinds = new(StringComparer.Ordinal)
        { "scopeprobe", "ordersweep-probe", "probe-emit", "probe-json" };

    // text[:n], counted in characters as Python counts them.
    static string Head(string s, int n)
    {
        int i = 0;
        for (int count = 0; i < s.Length && count < n; count++)
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
        return s[..i];
    }

    // str(v)[:12].
    static string Head12(object? v) => Head(Ev.Str(v), 12);

    /// <summary>The rejection detail of a source that names another candidate than the reference arm's.</summary>
    public static string SourceMismatch(string source, string wantSource) =>
        $"the emit's Configuration.Source is '{source}', not this arm's '{wantSource}'";

    /// <summary>A text field read as Python's `(v or "")` with a string's methods.</summary>
    public static string TextOrEmpty(object? v) => !PyValue.Truthy(v) ? ""
        : v as string ?? throw new PyError($"'{PyValue.TypeName(v)}' object has no attribute 'strip'");

    // d.get(upper, d.get(lower)).
    static object? Either(PyDict d, string upper, string lower) => Ev.G(d, upper, d.Get(lower));

    // cfg.get(upper, cfg.get(lower, []) or []): the upper-case key as it is, even null.
    static object? EitherList(PyDict d, string upper, string lower) =>
        d.ContainsKey(upper) ? d[upper] : Ev.OrList(Ev.G(d, lower, new List<object?>()));

    static PyDict Rejection(string cause, string detail) => new() { ["cause"] = cause, ["detail"] = detail };

    /// <summary>Which arm's frozen request an emit is compared against: a reverse part ran on the baseline order, so
    /// the baseline's request is its reference.</summary>
    public PyDict ReferenceArm(PyDict arm, string? part)
    {
        if (part == "reverse")
            foreach (var a in PyValue.Iter(Ev.G(Plan, "arms", new List<object?>())))
                if (Ev.Eq(Ev.G(a, "kind"), "baseline")) return PyValue.Dict(a);
        return arm;
    }

    /// <summary>scopeprobe's own wording for a named target the resolved configuration lacks.</summary>
    public static string ScopeAbsentTarget(string name) => $"'{name}' is not in the resolved configuration (";

    /// <summary>Whether a scopeprobe exit 1 on this arm is fully explained by targets its frozen request leaves out.</summary>
    public PyDict ScopeOmission(PyDict arm, string? part, object? docRaw)
    {
        var reference = ReferenceArm(arm, part);
        var o = new PyDict { ["legal"] = false, ["why"] = "", ["omitted"] = new List<object?>(), ["measured"] = new List<object?>(), ["coverage"] = null };
        if (docRaw is not PyDict doc)
        {
            o["why"] = "the scope JSON could not be read, so its walk failures cannot be classified";
            return o;
        }
        var failures = Ev.Items(doc.Get("walkFailures")).Select(Ev.Str).ToList();
        object? cfg = PyValue.Truthy(doc.Get("configuration")) ? doc["configuration"] : PyValue.Truthy(doc.Get("Configuration")) ? doc["Configuration"] : new PyDict();
        var cfgDict = PyValue.Dict(cfg);
        var resolved = Ev.Items(PyValue.Truthy(cfgDict.Get("plugins")) ? cfgDict["plugins"] : cfgDict.Get("Plugins"))
            .OfType<PyDict>().Select(p => Either(p, "Name", "name") is var n && PyValue.Truthy(n) ? Ev.Str(n) : "").ToList();
        object? reqRows = Ev.OrEmpty(reference.Get("requested")).Get("plugins");
        var requested = reqRows is List<object?> rows
            ? rows.OfType<PyDict>().Select(p => PyValue.Truthy(p.Get("name")) ? Ev.Str(p["name"]) : "").ToList()
            : Ev.Items(reference.Get("plugins")).Select(Ev.Str).ToList();
        var targets = Ev.Items(Plan.Get("targets")).OfType<PyDict>().Where(t => PyValue.Truthy(t.Get("name"))).Select(t => Ev.Str(t["name"])).ToList();
        var reqLow = requested.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
        var resLow = resolved.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
        var omitted = targets.Where(t => !reqLow.Contains(PyText.Lower(t))).ToList();
        var loaded = targets.Where(t => reqLow.Contains(PyText.Lower(t))).ToList();
        var measured = Ev.Items(doc.Get("targets")).OfType<PyDict>().Select(t => PyValue.Truthy(t.Get("name")) ? Ev.Str(t["name"]) : "").ToList();
        o["omitted"] = Py.Objs(omitted);
        o["measured"] = Py.Objs(measured);
        var unexplained = failures.Where(f => !omitted.Any(t => f.StartsWith(ScopeAbsentTarget(t), StringComparison.Ordinal)
                                                                 && !resLow.Contains(PyText.Lower(t)))).ToList();
        if (unexplained.Count > 0)
            o["why"] = "walk failure(s) that are not a target this arm's request leaves out: " + string.Join("; ", unexplained);
        else if (failures.Count == 0)
            o["why"] = "the probe named no walk failure, so its incompleteness is not a target this arm leaves out";
        else if (resolved.Count == 0 || !resolved.Select(PyText.Lower).SequenceEqual(requested.Select(PyText.Lower)))
            o["why"] = $"the resolved configuration ({resolved.Count} plugins) is not this arm's frozen "
                       + $"request ({requested.Count} plugins), so what loaded is not the order the plan approved";
        else if (!PyOrder.SortedByCodePoint(measured.Select(PyText.Lower)).SequenceEqual(PyOrder.SortedByCodePoint(loaded.Select(PyText.Lower))))
        {
            var got = measured.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
            o["why"] = "a plan target this arm's request loads was not measured: " + string.Join(", ", loaded.Where(t => !got.Contains(PyText.Lower(t))));
        }
        else
        {
            o["legal"] = true;
            string it = omitted.Count == 1 ? "it" : "them";
            o["coverage"] = new PyDict
            {
                ["Population"] = "target footprints", ["Judged"] = (long)measured.Count,
                ["Unjudgeable"] = new PyDict { ["not-loaded-by-this-arm"] = (long)omitted.Count },
                ["Note"] = $"the order resolved to this arm's frozen request ({resolved.Count} plugins); that is "
                           + "this check's loadability answer. Not measured on this arm: "
                           + string.Join(", ", omitted) + $" - the arm's request does not load {it}, so no footprint "
                           + $"of {it} was requested or measured here, and nothing in this receipt describes {it}.",
            };
        }
        return o;
    }

    // ---- readers

    /// <summary>The configuration an emit states it consumed, in the receipt's spelling; null when it states none.</summary>
    public static PyDict? EmitIdentity(PyDict doc)
    {
        object? raw = PyValue.Truthy(doc.Get("Configuration")) ? doc["Configuration"] : doc.Get("configuration");
        if (!PyValue.Truthy(raw)) return null;
        var cfg = PyValue.Dict(raw);
        return new PyDict
        {
            ["fingerprint"] = Ev.G(cfg, "Fingerprint", Ev.G(cfg, "fingerprint", "")),
            ["source"] = Ev.G(cfg, "Source", Ev.G(cfg, "source", "")),
            ["schemaVersion"] = Either(cfg, "SchemaVersion", "schemaVersion"),
            ["plugins"] = PyValue.Iter(EitherList(cfg, "Plugins", "plugins")).Select(PyValue.Dict).Select(p => (object?)new PyDict
            {
                ["name"] = Either(p, "Name", "name"), ["path"] = Either(p, "Path", "path"), ["sha256"] = Either(p, "Sha256", "sha256"),
            }).ToList(),
            ["providers"] = PyValue.Iter(EitherList(cfg, "Providers", "providers")).Select(PyValue.Dict).Select(v => (object?)new PyDict
            {
                ["name"] = Either(v, "Name", "name"), ["kind"] = Either(v, "Kind", "kind"),
            }).ToList(),
            ["overlays"] = PyValue.Iter(EitherList(cfg, "Overlays", "overlays")).Select(PyValue.Dict).Select(v => (object?)new PyDict
            {
                ["kind"] = Either(v, "Kind", "kind"), ["path"] = Either(v, "Path", "path"), ["sha256"] = Either(v, "Sha256", "sha256"),
            }).ToList(),
            ["population"] = Either(cfg, "Population", "population"),
            ["state"] = Either(cfg, "State", "state"),
            ["limits"] = EitherList(cfg, "Limits", "limits"),
        };
    }

    /// <summary>The emit's coverage lines.</summary>
    /// <summary>A conflict file's coverage that assessed nothing (its first population judged none and could not judge some,
    /// or every population judged none): the rejection's detail, or null when something was assessed.</summary>
    public static string? NothingAssessed(IReadOnlyList<object?> coverage)
    {
        var first = PyValue.Dict(coverage[0]);
        bool allZero = coverage.All(c => Judged(c).IsZero);
        if (!((Judged(first).IsZero && Unjudgeable(first) > 0) || allZero)) return null;
        return $"population '{Ev.Str(Either(first, "Population", "population"))}' judged {Judged(first)} with {Unjudgeable(first)} "
               + "unjudgeable: zero findings here is zero assessed subjects";
    }

    public static List<object?> CoverageLines(PyDict doc)
    {
        object? v = PyValue.Truthy(doc.Get("Coverage")) ? doc["Coverage"] : doc.Get("coverage");
        return PyValue.Truthy(v) ? PyValue.Iter(v).ToList() : [];
    }

    /// <summary>How many subjects a coverage line judged.</summary>
    public static BigInteger Judged(object? line)
    {
        var d = PyValue.Dict(line);
        object? v = Ev.G(d, "Judged", Ev.G(d, "judged", 0L));
        return PyValue.Truthy(v) ? PyValue.Int(v) : 0;
    }

    /// <summary>How many subjects a coverage line could not judge.</summary>
    public static BigInteger Unjudgeable(object? line) => Disposition.Unjudgeable(PyValue.Dict(line));

    /// <summary>A registry result spec read against an output: a dotted path into the JSON, `regex:PATTERN` with a
    /// group `n` over the child's log, or a list of dotted paths summed. (count, why it did not resolve).</summary>
    public static (object? value, string why) ResolveSpec(object? spec, object? doc, string? logText)
    {
        if (spec is List<object?> parts)
        {
            if (parts.Count == 0 || !parts.All(x => x is string s && s.Length > 0 && !s.StartsWith("regex:", StringComparison.Ordinal)))
                return (null, "a list spec must hold dotted paths only");
            BigInteger total = 0;
            foreach (var part in parts)
            {
                var (val, why) = ResolveSpec(part, doc, logText);
                if (val == null) return (null, $"{PyText.Repr(part)}: {why}");
                total += PyValue.Int(val);
            }
            return (PyValue.Whole(total), "");
        }
        if (spec is not string text) return (null, "the spec is not a string");
        if (text.StartsWith("regex:", StringComparison.Ordinal))
        {
            Match m;
            try { m = PyRegex.Search(text["regex:".Length..], logText ?? "", multiline: true); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or Refusal)
            {
                return (null, $"the pattern does not compile: {ex.Message}");
            }
            if (!m.Success) return (null, "the pattern matched nothing in the child's log");
            var n = m.Groups["n"];
            var number = n.Success ? PyValue.IntText(n.Value.Replace(",", "")) : null;
            return number is { } whole ? (PyValue.Whole(whole), "") : (null, "the pattern matched but its group 'n' is not a number");
        }
        if (doc == null) return (null, "the output is not JSON, so a dotted path cannot be read");
        object? v = PyJson.Dotted(doc, text);
        if (v is bool) return (null, "the path resolved to a boolean, not a count");
        if (PyValue.IsInt(v)) return (v, "");
        if (v is List<object?> or PyDict) return ((long)PyValue.Len(v), "");
        return (null, "the path is absent from this output");
    }

    // judged == 0 and (unjudgeable or 0) > 0.
    static bool NoneJudgedSomeNot(object? judged, object? unjudgeable) =>
        judged != null && PyValue.Eq(judged, 0L) && PyValue.Truthy(unjudgeable) && PyValue.AboveZero(unjudgeable);

    // A file's st_mtime as Python's float.
    static double Mtime(string path) => Receipts.MtimeSeconds(path);

    // The stale-output test of one file a child was to write: older than the launch, or the bytes that were there before.
    void StaleCheck(string path, IReadOnlyDictionary<string, string?> preHashes, double started, List<object?> rej)
    {
        double mtime = Mtime(path);
        preHashes.TryGetValue(path, out string? pre);
        if (mtime + 2.0 < started)
            rej.Add(Rejection("output-stale", $"{PyPath.RelTo(Run, path)} was last written {PyText.IsoOf(mtime)}, "
                                              + $"before the child started {PyText.IsoOf(started)}"));
        else if (pre != null && Hashing.Sha256FileOrNull(path) == pre)
            rej.Add(Rejection("output-stale", $"{PyPath.RelTo(Run, path)} is byte-identical to the file that "
                                              + "existed before the child started: nothing was produced"));
    }

    // ---- the checks

    /// <summary>One child's result judged: {accepted, rejections, detector, findings, coverage, verdict, identity,
    /// identityLimits}. `started` is the wall-clock second the child was launched at.</summary>
    public PyDict Validate(PyDict check, PyDict arm, string? part, PyDict adapter, long? exitCode, string? logText,
        IReadOnlyList<string> outputs, IReadOnlyDictionary<string, string?> preHashes, double? started)
    {
        var rej = new List<object?>();
        var identityLimits = new List<object?>();
        object? outKind = PyValue.Truthy(adapter.Get("output")) ? adapter["output"] : "conflict-file";
        string key = Ledger.Key(check["checkId"], arm["armId"], part);
        if (_seenKeys.Contains(key))
            rej.Add(Rejection("duplicate-receipt", $"{key} was already receipted in this invocation"));
        _seenKeys.Add(key);
        string log = logText ?? "";

        string? primary = outputs.Count > 0 ? outputs[0] : null;
        bool produced = primary != null && Ev.Exists(primary);
        // a refusal is a marker plus a failure to deliver: a probe that exited 0 and wrote a valid emit did not refuse
        var marker = RefusedRe.Match(log);
        bool failedExit = exitCode is not (null or 0);
        bool refused = marker.Success && (failedExit || !produced);
        bool orderKind = adapter.Get("kind") is string k && OrderConsumingKinds.Contains(k);

        // scopeprobe's exit 1 means written but incomplete; an arm that leaves a plan target out always exits 1
        PyDict? omission = null;
        if (Ev.Eq(adapter.Get("kind"), "scopeprobe") && exitCode == 1 && produced && !refused && Ev.Eq(outKind, "json"))
            omission = ScopeOmission(arm, part, PyJson.LoadEvalFile(primary!));
        bool legalOmission = omission != null && PyValue.Truthy(omission["legal"]);

        if (exitCode == 2 && refused && !produced && orderKind)
        {
            string line = PyText.SplitLines(log).Where(ln => RefusedRe.IsMatch(ln)).Select(PyValue.Strip).FirstOrDefault() ?? "";
            rej.Add(Rejection("illegal-configuration",
                "the child refused the CONFIGURATION itself (exit 2, a refusal marker, no output): the game would not load it as written"
                + (line.Length > 0 ? $" - the child's refusal: {PyText.Repr(Head(line, 300))}" : "")));
        }
        else if (refused)
        {
            rej.Add(Rejection("refused", $"refusal marker in the child's log (stderr included): {PyText.Repr(PyValue.Strip(marker.Value))}"));
            if (failedExit) rej.Add(Rejection("exit-nonzero", $"exit code {exitCode}"));
        }
        else if (legalOmission) { }
        else if (failedExit)
            rej.Add(Rejection("exit-nonzero", $"exit code {exitCode}" + (omission != null ? $": {Ev.Str(omission["why"])}" : "")));

        object? doc = null;
        object? detector = null, findings = null;
        var coverage = new List<object?>();
        PyDict? identity = null;

        foreach (string p in outputs)
            if (!Ev.Exists(p)) rej.Add(Rejection("output-missing", PyPath.RelTo(Run, p)));

        if (produced && started is { } since) StaleCheck(primary!, preHashes, since, rej);

        bool text = Ev.Eq(outKind, "text");
        if (text)
        {
            produced = true;   // the log is the output; output-missing does not apply to it
            foreach (string p in outputs.Skip(1))
                // adapter.files: written by this child, never left over from an earlier run
                if (Ev.Exists(p) && started is { } t0) StaleCheck(p, preHashes, t0, rej);
        }
        if (produced && !text)
        {
            doc = PyJson.LoadEvalFile(primary!);
            if (doc is not PyDict d)
                rej.Add(Rejection("schema-unsupported", $"{PyPath.RelTo(Run, primary!)} is not a JSON object"));
            else if (Ev.Eq(outKind, "conflict-file"))
            {
                if (!d.ContainsKey("Probe") || !d.ContainsKey("Conflicts"))
                    rej.Add(Rejection("schema-unsupported", "not a conflict file: no Probe / Conflicts"));
                else
                {
                    object? exe = adapter.Get("exe");
                    if (PyValue.Truthy(exe) && PyText.Lower(Ev.Str(Ev.G(d, "Probe", ""))) != PyText.Lower(Ev.Str(exe)))
                        rej.Add(Rejection("wrong-check", $"the emit says Probe='{Ev.Str(d.Get("Probe"))}' but the adapter ran '{Ev.Str(exe)}'"));
                    identity = EmitIdentity(d);
                    if (identity == null)
                        rej.Add(Rejection("identity-missing", "the emit carries no Configuration: what it consumed cannot be stated"));
                    else
                        rej.AddRange(IdentityChecks(identity, arm, d, adapter, part, identityLimits));
                    coverage = CoverageLines(d);
                    if (coverage.Count == 0)
                        rej.Add(Rejection("coverage-missing", "the emit declares no Coverage: what was judged is unknown"));
                    else if (NothingAssessed(coverage) is { } none)
                        rej.Add(Rejection("extraction-failed", none));
                    findings = (long)(PyValue.Truthy(d.Get("Conflicts")) ? PyValue.Len(d["Conflicts"]) : 0);
                }
            }
            else if (Ev.Eq(adapter.Get("kind"), "scopeprobe") && d.ContainsKey("complete"))
            {
                // scope.json's own `complete` is the probe's contract
                findings = PyValue.Truthy(d.Get("complete")) ? 0L : null;
                if (legalOmission)
                {
                    // the order resolved to exactly this arm's request, so its loadability is answered
                    findings = 0L;
                    coverage = [omission!["coverage"]];
                }
            }
            else
            {
                // a json output is read only where the registry's adapter.result says its numbers live
                var spec = Ev.OrEmpty(adapter.Get("result"));
                object? judged = null, unjudgeable = null;
                foreach (string name in new[] { "findings", "judged", "unjudgeable" })
                {
                    object? raw = spec.Get(name);
                    if (raw == null) continue;
                    var (val, why) = ResolveSpec(raw, doc, log);
                    if (val == null)
                    {
                        rej.Add(Rejection("schema-unsupported", $"adapter.result.{name} = {PyText.Repr(raw)} did not resolve against this output: {why}"));
                        continue;
                    }
                    if (name == "findings") findings = val;
                    else if (name == "judged") judged = val;
                    else unjudgeable = val;
                }
                if (NoneJudgedSomeNot(judged, unjudgeable))
                    rej.Add(Rejection("extraction-failed", $"adapter.result judged 0 with {Ev.Str(unjudgeable)} unjudgeable: zero rows here is zero assessed subjects"));
                if (judged != null)
                    coverage = [new PyDict
                    {
                        ["Population"] = Ev.G(spec, "unit", "rows"), ["Judged"] = judged,
                        ["Unjudgeable"] = PyValue.Truthy(unjudgeable) ? new PyDict { ["unjudgeable"] = unjudgeable } : new PyDict(),
                        ["Note"] = "from adapter.result",
                    }];
            }
        }

        if (text)
        {
            // a text probe's numbers live in its own printed lines; the registry's regex specs say where
            var spec = Ev.OrEmpty(adapter.Get("result"));
            object? judged = null, unjudgeable = null;
            foreach (string name in new[] { "findings", "judged", "unjudgeable" })
            {
                object? raw = spec.Get(name);
                if (raw == null) continue;
                var (val, why) = ResolveSpec(raw, null, log);
                if (val == null)
                {
                    rej.Add(Rejection("schema-unsupported", $"adapter.result.{name} = {PyText.Repr(raw)} did not resolve against this probe's printed output: {why}"));
                    continue;
                }
                if (name == "findings") findings = val;
                else if (name == "judged") judged = val;
                else unjudgeable = val;
            }
            if (NoneJudgedSomeNot(judged, unjudgeable))
                rej.Add(Rejection("extraction-failed", $"adapter.result judged 0 with {Ev.Str(unjudgeable)} unjudgeable"));
            if (judged != null)
                coverage = [new PyDict
                {
                    ["Population"] = Ev.G(spec, "unit", "rows"), ["Judged"] = judged,
                    ["Unjudgeable"] = PyValue.Truthy(unjudgeable) ? new PyDict { ["unjudgeable"] = unjudgeable } : new PyDict(),
                    ["Note"] = "from adapter.result over the child's printed output",
                }];
        }

        bool accepted = rej.Count == 0;
        if (accepted)
        {
            object? resultKind = !Ev.Eq(outKind, "conflict-file") && !Ev.Eq(adapter.Get("kind"), "scopeprobe")
                ? Ev.OrEmpty(adapter.Get("result")).Get("kind") : "detector";
            // a catalogue's rows are evidence, not defects: it completes, and is neither a finding nor a null check
            if (resultKind is "catalogue" || resultKind is string more && MoreCatalogueKinds.Contains(more)) detector = "catalogue";
            else if (findings == null) detector = null;
            else detector = PyValue.AboveZero(findings) ? "finding" : "no-finding";
            if (Ev.Eq(adapter.Get("kind"), "scopeprobe") && doc is PyDict sd && sd.ContainsKey("complete") && !PyValue.Truthy(sd.Get("complete")) && !legalOmission)
                detector = "inconclusive";
            if (PyValue.Truthy(identity))
            {
                var reference = ReferenceArm(arm, part);
                _armFingerprints.TryAdd((Ev.Str(reference["armId"]), Ev.Str(identity!.Get("population")), Ev.Str(identity.Get("state"))), identity["fingerprint"]);
            }
        }
        return new PyDict
        {
            ["accepted"] = accepted, ["rejections"] = rej, ["detector"] = detector, ["findings"] = findings,
            ["coverage"] = coverage, ["verdict"] = null, ["identity"] = identity, ["identityLimits"] = identityLimits,
        };
    }

    /// <summary>The fingerprint of a stated configuration by the probes' own rule (ConfigurationIdentity.Compute): a
    /// null population or state adds no text, as the owner writes it, and the schema is the owner's.</summary>
    public static string Fingerprint(IEnumerable<object?> plugins, object? providers, object? overlays, object? population, object? state)
    {
        static string Text(object? v) => PyValue.Truthy(v) ? Ev.Str(v) : "";
        var rows = plugins.Select(PyValue.Dict).Select(p => new ConfigPlugin(Text(p.Get("name")), "", Text(p.Get("sha256")), 0, "", false, false, [])).ToList();
        var views = Ev.Items(providers).Select(PyValue.Dict).Select(v => new ConfigProvider(Text(v.Get("name")), Text(v.Get("kind")))).ToList();
        var over = Ev.Items(overlays).Select(PyValue.Dict).Select(o => new ConfigOverlay(Text(o.Get("kind")), "", Text(o.Get("sha256")))).ToList();
        return ConfigurationIdentity.Compute("", rows, views, over, population == null ? null! : Ev.Str(population),
                                             state == null ? null! : Ev.Str(state)).Fingerprint;
    }

    // sorted((str(kind or ""), str(sha256 or "").lower()) for each overlay).
    static List<object?> OverlayKey(object? rows) =>
        Ev.Items(rows).Select(PyValue.Dict)
            .Select(o => (kind: PyValue.Truthy(o.Get("kind")) ? Ev.Str(o["kind"]) : "", sha: PyText.Lower(PyValue.Truthy(o.Get("sha256")) ? Ev.Str(o["sha256"]) : "")))
            .OrderBy(x => x.kind, PyOrder.CodePoint).ThenBy(x => x.sha, PyOrder.CodePoint)
            .Select(x => (object?)new PyTuple([x.kind, x.sha])).ToList();

    /// <summary>An accepted emit must describe the configuration the plan asked for: the rejections, with what was
    /// accepted under a stated limit added to `limits`.</summary>
    public List<object?> IdentityChecks(PyDict identity, PyDict arm, PyDict doc, PyDict? adapter, string? part = null,
        List<object?>? limits = null)
    {
        var rej = new List<object?>();
        var note = limits ?? [];
        var reference = ReferenceArm(arm, part);
        var req = Ev.OrEmpty(reference.Get("requested"));
        var reqPlugins = Ev.Items(req.Get("plugins")).Select(PyValue.Dict).ToList();
        var emitPlugins = Ev.Items(identity.Get("plugins")).Select(PyValue.Dict).ToList();

        // (a) an incomplete identity is never accepted
        if (emitPlugins.Count == 0)
        {
            rej.Add(Rejection("identity-missing", "the emit's Configuration lists no plugins: what it consumed cannot be stated, so it cannot be compared to the request"));
            return rej;
        }
        for (int i = 0; i < emitPlugins.Count; i++)
        {
            var p = emitPlugins[i];
            if (PyValue.Strip(TextOrEmpty(p.Get("name"))).Length == 0)
                rej.Add(Rejection("identity-missing", $"the emit's Configuration plugin at position {i} has no name"));
            else if (!PlanArms.IsSha256(p.Get("sha256")))
                rej.Add(Rejection("identity-missing", $"the emit's Configuration gives no 64-hex sha256 for {Ev.Str(p.Get("name"))} (position {i}): its content is not stated"));
        }
        if (rej.Count > 0) return rej;

        if (reqPlugins.Count == 0)
        {
            rej.Add(Rejection("identity-missing", $"the plan states no requested plugin list for arm {Ev.Str(reference["armId"])}, so this emit cannot be bound to a request"));
            return rej;
        }

        // (b) order and names, without case, position by position
        static string NameOf(PyDict p) => PyValue.Truthy(p.Get("name")) ? Ev.Str(p["name"]) : "";
        var wantNames = reqPlugins.Select(NameOf).ToList();
        var gotNames = emitPlugins.Select(NameOf).ToList();
        if (!wantNames.Select(PyText.Lower).SequenceEqual(gotNames.Select(PyText.Lower)))
        {
            var wantSet = wantNames.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
            var gotSet = gotNames.Select(PyText.Lower).ToHashSet(StringComparer.Ordinal);
            var extra = PyOrder.SortedByCodePoint(gotSet.Except(wantSet));
            var absent = PyOrder.SortedByCodePoint(wantSet.Except(gotSet));
            static string At(List<string> names, int i, string none) => i < names.Count ? names[i] : none;
            int first = 0;
            for (int i = 0; i < Math.Max(wantNames.Count, gotNames.Count); i++)
                if (PyText.Lower(At(wantNames, i, "")) != PyText.Lower(At(gotNames, i, ""))) { first = i; break; }
            var parts = new List<string>();
            if (absent.Count > 0) parts.Add($"missing: {string.Join(", ", absent)}");
            if (extra.Count > 0) parts.Add($"extra: {string.Join(", ", extra)}");
            string how = extra.Count == 0 && absent.Count == 0 ? "the same names in a different order" : "the set differs (" + string.Join("; ", parts) + ")";
            rej.Add(Rejection("identity-mismatch", $"plugin order differs from the request at position {first + 1}: "
                + $"expected {At(wantNames, first, "(nothing)")}, found {At(gotNames, first, "(nothing)")}; {how}"));
        }
        else
        {
            // (c) hashes, wherever the request states one; an unstated hash is a recorded limit, never a silent acceptance
            foreach (var (want, got) in reqPlugins.Zip(emitPlugins))
            {
                if (Ev.Eq(want.Get("hashSource"), "unstated") || !PlanArms.IsSha256(want.Get("sha256")))
                {
                    note.Add(new PyDict
                    {
                        ["plugin"] = want.Get("name"), ["recordedSha256"] = got.Get("sha256"),
                        ["detail"] = $"the plan could not state a hash for {Ev.Str(want.Get("name"))}; the emit's "
                                     + $"hash {Head12(got.Get("sha256"))} is recorded and is what reuse re-checks on disk",
                    });
                    continue;
                }
                static string PathOr(PyDict row) => PyValue.Truthy(row.Get("path")) ? Ev.Str(row["path"]) : "(no path)";
                if (PyText.Lower(Ev.Str(want["sha256"])) != PyText.Lower(PyValue.Truthy(got.Get("sha256")) ? Ev.Str(got["sha256"]) : ""))
                    rej.Add(Rejection("identity-mismatch", $"plugin {Ev.Str(want.Get("name"))} was requested at "
                        + $"{Head12(want["sha256"])} ({PathOr(want)}) and consumed at {Head12(got.Get("sha256"))} ({PathOr(got)})"));
                else if (PyValue.Truthy(want.Get("path")) && PyValue.Truthy(got.Get("path")) && !PyOrder.SamePath(Ev.Str(want["path"]), Ev.Str(got["path"])))
                    note.Add(new PyDict
                    {
                        ["plugin"] = want.Get("name"), ["recordedSha256"] = got.Get("sha256"),
                        ["plannedPath"] = want.Get("path"), ["emitPath"] = got.Get("path"),
                        ["detail"] = $"the plan resolved {Ev.Str(want.Get("name"))} at {Ev.Str(want["path"])} and the emit "
                                     + $"read an identical copy at {Ev.Str(got["path"])}: the bytes are the same "
                                     + $"{Head12(got.Get("sha256"))}, the file is not",
                    });
            }
        }

        // (d) overlays as a multiset of (kind, sha256)
        var gotOverlays = OverlayKey(identity.Get("overlays"));
        var wantOverlays = OverlayKey(req.Get("overlays"));
        if (!PyValue.Eq(gotOverlays, wantOverlays))
            rej.Add(Rejection("identity-mismatch", $"the emit's overlays are {PyText.Repr(gotOverlays)}, arm "
                + $"{Ev.Str(reference["armId"])} requested {PyText.Repr(wantOverlays)}"));

        // (e) the fingerprint must be the digest of the configuration the emit itself states
        string recomputed = Fingerprint(emitPlugins, identity.Get("providers"), identity.Get("overlays"), identity.Get("population"), identity.Get("state"));
        if (PyText.Lower(PyValue.Truthy(identity.Get("fingerprint")) ? Ev.Str(identity["fingerprint"]) : "") != recomputed)
            rej.Add(Rejection("identity-mismatch", "the fingerprint is not the digest of the stated configuration: the "
                + $"emit says {Head12(identity.Get("fingerprint"))}, its own plugins, "
                + $"providers, overlays, population and state digest to {Head(recomputed, 12)}"));

        // (f) the source must name the reference arm's candidate
        string source = TextOrEmpty(identity.Get("source"));
        object? candidateFile = reference["candidateFile"];
        string wantSource = $"candidate:{Ev.Str(candidateFile)}";
        if (source.Length > 0 && PyValue.Truthy(candidateFile) && PyText.Lower(source) != PyText.Lower(wantSource)
                 && !PyText.Lower(source).Contains(PyText.Lower(Ev.Stem(Ev.Str(candidateFile))), StringComparison.Ordinal))
            rej.Add(Rejection("identity-mismatch", SourceMismatch(source, wantSource)));

        // (g) the within-arm rules, keyed on the reference arm
        var key = (Ev.Str(reference["armId"]), Ev.Str(identity.Get("population")), Ev.Str(identity.Get("state")));
        _armFingerprints.TryGetValue(key, out object? prior);
        object? fingerprint = identity["fingerprint"];
        if (PyValue.Truthy(prior) && PyValue.Truthy(fingerprint) && !PyValue.Eq(prior, fingerprint))
            rej.Add(Rejection("identity-mismatch", $"fingerprint {Head12(fingerprint)} differs from this arm's "
                + $"other accepted receipts declaring the same population and state ({Head12(prior)})"));
        string armKey = Ev.Str(reference["armId"]);
        if (!_armPluginHashes.TryGetValue(armKey, out var seen)) _armPluginHashes[armKey] = seen = new(StringComparer.Ordinal);
        foreach (var p in PyValue.Iter(Ev.G(identity, "plugins", new List<object?>())).Select(PyValue.Dict))
        {
            string name = PyText.Lower(NameOf(p));
            object? h = p.Get("sha256");
            if (name.Length == 0 || !PyValue.Truthy(h)) continue;
            if (seen.TryGetValue(name, out object? was) && !PyValue.Eq(was, h))
                rej.Add(Rejection("mixed-snapshot", $"plugin {name} was consumed as {Head12(was)} by another accepted "
                    + $"receipt of this arm and as {Head12(h)} here: the inputs changed during "
                    + "the run and cannot be mixed into one snapshot"));
            seen.TryAdd(name, h);
        }
        var ad = adapter ?? new PyDict();
        object? declaredPop = ad.Get("population"), declaredState = ad.Get("state");
        var expected = Ev.OrEmpty(arm.Get("expected"));
        object? wantPop = declaredPop ?? expected.Get("population");
        static object? FirstTruthy(params object?[] values) => values.FirstOrDefault(PyValue.Truthy) ?? values[^1];
        object? gotPop = FirstTruthy(identity.Get("population"), doc.Get("Population"), doc.Get("population"));
        object? gotState = FirstTruthy(identity.Get("state"), doc.Get("State"), doc.Get("state"));
        var wantStates = declaredState != null ? [declaredState]
            : PyValue.Truthy(expected.Get("states")) ? PyValue.Iter(expected["states"]).ToList() : ["baseline"];
        if (PyValue.Truthy(wantPop) && PyValue.Truthy(gotPop) && Ev.Str(gotPop) != Ev.Str(wantPop))
            rej.Add(Rejection("population-mismatch", $"the emit judged population '{Ev.Str(gotPop)}', the plan's arm is '{Ev.Str(wantPop)}'"));
        if (PyValue.Truthy(gotState) && !wantStates.Select(Ev.Str).Contains(Ev.Str(gotState)) && Ev.Str(gotState) != "-")
            rej.Add(Rejection("population-mismatch", $"the emit judged state '{Ev.Str(gotState)}', the plan selected "
                + string.Join(", ", wantStates.Select(Ev.Str))));
        return rej;
    }
}
