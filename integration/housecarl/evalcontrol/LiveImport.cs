using RunControl;

namespace EvalControl;

/// <summary>`evalcontrol import` for a `live-import` check (moved from runcontrol with the check): a live game driver's receipts (evallive.py's import_live, ported
/// as the one owner of the rule; the first such check is `runtime.state-switch-flip`, fed by switchflip.py).
///
/// What it refuses to trust. A live receipt says what the game did, so every claim in it is re-derived here:
///   - each launch receipt names the plan, check and arm it was launched for (written by the driver BEFORE the launch),
///     and every evidence file is hash-checked;
///   - each launch's classification is recomputed from its recorded events by SwitchFlipOutcome.Classify; a stored
///     label that differs is refused, never preferred;
///   - the arm's reading is recomputed by SwitchFlipOutcome.ArmReading, per condition: an unsettled condition (one
///     failure beside one completion, or fewer than two measured launches) cannot be imported;
///   - the configuration the driver measured must equal the arm's frozen request, by the validator's identity rules.
///
/// Import document (schema 1), written by `switchflip.py export`:
///   {schemaVersion: 1, planId, checkId, armId, capturedUtc, tool: {name, version?}, state, configuration,
///    result: {detector, findings, coverage: [{Population, Judged, Unjudgeable}]},
///    outputs: [{role: flip-plan|launch-receipt|events|crash-log, path, sha256}]}
/// ImportResults has already checked the document's schema, plan id and check before it hands over.</summary>
public static class LiveImport
{
    static readonly string[] Roles = ["flip-plan", "launch-receipt", "events", "crash-log"];
    const long LaunchSchema = 1;

    static int Refuse(string message)
    {
        Console.Error.WriteLine($"REFUSED: {message}");
        return Exit.Refused;
    }

    // _parse_iso: an aware ISO-8601 instant, or null
    static PyDateTime.Stamp? Iso(object? s) =>
        s is string text && PyDateTime.FromIso(text.Replace("Z", "+00:00")) is { Aware: true } stamp ? stamp : null;

    static string S(object? v) => PyText.Str(v);

    /// <summary>A launch's events file: one JSON object per non-blank line.</summary>
    public static List<object?> ReadEvents(string path) =>
        PyText.SplitLines(PyJson.Decode(File.ReadAllBytes(path), bom: false))
            .Where(line => PyValue.Strip(line).Length > 0).Select(line => PyJson.Loads(line)).ToList();

    /// <summary>(errors, per-condition readings) for [(launch receipt, its events)]: each launch re-read from its events,
    /// a stored label that differs refused, an unsettled condition refused.</summary>
    public static (List<string> errors, PyDict readings) RereadLaunches(IEnumerable<(PyDict receipt, List<object?> events)> launches)
    {
        var errors = new List<string>();
        var byCond = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        foreach (var (rc, events) in launches)
        {
            string lid = $"{S(rc.Get("condition"))}-{S(rc.Get("replicate"))}";
            object? flipStart = rc.Get("flipStart"), window = rc.Get("windowSeconds");
            PyDict cls;
            if (flipStart == null)
                cls = new PyDict { ["outcome"] = "not-measured", ["branch"] = null, ["why"] = "the flip was never started" };
            else if (window is not (bool or long or int or System.Numerics.BigInteger or double) || ToDouble(window) < SwitchFlipOutcome.WindowS)
            {
                errors.Add($"launch {lid}: windowSeconds {PyText.Repr(window)} is below the rule's {PyText.General(SwitchFlipOutcome.WindowS)} s");
                continue;
            }
            else
                cls = SwitchFlipOutcome.Classify(events, ToDouble(flipStart), ToDouble(window));
            var stored = Ev.OrEmpty(rc.Get("classification"));
            if (!Ev.Eq(stored.Get("outcome"), cls["outcome"]) || !Ev.Eq(stored.Get("branch"), cls["branch"]))
            {
                errors.Add($"launch {lid}: the stored classification {S(stored.Get("outcome"))}/{S(stored.Get("branch"))} differs from its events "
                           + $"re-read ({S(cls["outcome"])}/{S(cls["branch"])}: {S(cls["why"])}); the stored label is never preferred");
                continue;
            }
            string cond = S(rc.Get("condition"));
            if (!byCond.TryGetValue(cond, out var list)) byCond[cond] = list = [];
            list.Add(cls);
        }
        var readings = new PyDict();
        foreach (string c in PyOrder.SortedByCodePoint(byCond.Keys)) readings[c] = SwitchFlipOutcome.ArmReading(byCond[c]);
        foreach (var (c, r) in readings)
            if (Ev.Eq(PyValue.Dict(r)["reading"], "unsettled"))
                errors.Add($"condition {c}: unsettled ({S(PyValue.Dict(r)["why"])}); an unsettled arm cannot be imported");
        return (errors, readings);
    }

    // float(x) for the numbers a receipt holds; anything else is the shape error Python would raise
    static double ToDouble(object? v) => v switch
    {
        bool b => b ? 1 : 0,
        long l => l,
        int i => i,
        System.Numerics.BigInteger n => (double)n,
        double d => d,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => throw new PyError($"a number was expected, found {PyText.Repr(v)}"),
    };

    /// <summary>The arm's result from its readings: a finding where any condition re-read as one.</summary>
    public static PyDict DeriveResult(PyDict readings)
    {
        long findings = readings.Count(kv => Ev.Eq(PyValue.Dict(kv.Value)["reading"], "finding"));
        var coverage = new List<object?>();
        foreach (var (c, raw) in readings)
        {
            var r = PyValue.Dict(raw);
            coverage.Add(new PyDict
            {
                ["Population"] = $"switch-flip launches: {c}",
                ["Judged"] = PyValue.Int(r["failed"]) + PyValue.Int(r["completed"]),
                ["Unjudgeable"] = new PyDict().Update(PyValue.Dict(r["unjudgeable"])),
            });
        }
        return new PyDict
        {
            ["detector"] = findings > 0 ? "finding" : "no-finding", ["findings"] = findings, ["coverage"] = coverage, ["readings"] = readings,
        };
    }

    /// <summary>Validate one live import against the frozen plan and write a normal receipt.</summary>
    public static int Import(string run, string src, PyDict plan, Ledger ledger, PyDict check, PyDict? arm, PyDict doc, byte[] manifestBytes,
                             PyDict programs, string? runId, Action<int>? outputs, Action<string>? imported)
    {
        object? cid = check["checkId"], aid = doc.Get("armId");
        var planned = Ev.Items(check.Get("arms")).Select(PyValue.Dict).ToList();
        if (arm == null || !planned.Any(x => Ev.Eq(x.Get("armId"), aid)))
            return Refuse($"identity-mismatch: check {S(cid)} has no planned arm {PyText.Repr(aid)}");
        object? rowStatus = planned.First(x => Ev.Eq(x.Get("armId"), aid)).Get("status");
        if (!Ev.Eq(rowStatus, "live-import"))
        {
            string hint = Ev.Eq(rowStatus, EvalStore.NotOptedIn) ? $" (not opted in: plan again with --opt-in {S(cid)})" : "";
            return Refuse($"wrong-check: the plan records {S(cid)} as {PyText.Repr(rowStatus)} on arm {S(aid)}, not live-import, so it is not "
                          + $"waiting for a live receipt{hint}");
        }
        // the arm's own accepted census may already answer the check: nothing a script drives touches a target
        var scopeRead = Ev.Eq(cid, LiveScope.CheckId) ? LiveScope.Read(run, plan, ledger, aid) : null;
        if (PyValue.Truthy(scopeRead) && Ev.Eq(scopeRead!["status"], "not-applicable"))
            return Refuse($"wrong-check: {S(cid)} is not applicable on arm {S(aid)}: {SwitchFlipScope.Sentence(scopeRead)}. "
                          + "An import completes a check the run left open; it never overrides the census's answer");
        string checkId = S(cid), armId = S(aid);
        string key = Ledger.Key(cid, aid, null);
        if (EvalStore.IsTerminalOk(Ev.OrEmpty(ledger.Entry(cid, aid, null)).Get("status")))
            return Refuse($"duplicate-receipt: {key} already has accepted evidence");

        var errors = new List<string>();
        object? captured = doc.Get("capturedUtc"), tool = doc.Get("tool");
        if (Iso(captured) == null) errors.Add("capturedUtc must be an ISO-8601 timestamp with a timezone");
        if (tool is not PyDict toolDoc || !PyValue.Truthy(toolDoc.Get("name"))) errors.Add("the import must name tool.name");

        // ---- evidence files: hash-checked, roles closed
        var files = Roles.ToDictionary(r => r, _ => new List<(string path, string sha)>(), StringComparer.Ordinal);
        var rows = doc.Get("outputs") as List<object?>;
        if (rows == null || rows.Count == 0)
        {
            errors.Add("output-missing: outputs must list the flip plan, each launch receipt and its events");
            rows = [];
        }
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not PyDict row || row.Get("role") is not string role || !Roles.Contains(role)
                || row.Get("path") is not string stated || stated.Length == 0 || !PlanArms.IsSha256(row.Get("sha256")))
            {
                errors.Add($"output-missing: outputs[{i}] must name a role ({string.Join(", ", Roles)}), a path and a 64-hex sha256");
                continue;
            }
            string path = PyPath.Str(stated);
            if (!Ev.IsAbsolute(path)) path = PyPath.Join(Parent(src), stated);
            string want = PyText.Lower((string)row["sha256"]!);
            if (!File.Exists(path)) errors.Add($"output-missing: {path}");
            else if (Hashing.Sha256File(path) != want) errors.Add($"output-stale: {path} does not hash to the imported sha256");
            else if (Bundle.BinarySuffixes.Contains(PyText.Lower(PyPath.Suffix(path)))) errors.Add($"output-missing: {PyPath.Name(path)} is a binary excluded from bundles");
            else files[role].Add((path, want));
        }
        if (files["flip-plan"].Count != 1) errors.Add("output-missing: exactly one flip-plan output is required");
        if (files["launch-receipt"].Count == 0) errors.Add("output-missing: no launch receipt");
        if (errors.Count > 0) return Refuse("import-results refused: " + string.Join("; ", errors));

        string planSha = files["flip-plan"][0].sha;
        var eventsBySha = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (p, h) in files["events"]) eventsBySha[h] = p;
        var planCreated = Iso(plan.Get("createdUtc"));
        var launches = new List<(PyDict, List<object?>)>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, _) in files["launch-receipt"])
        {
            PyDict rc;
            try
            {
                if (PyJson.Loads(PyJson.Decode(File.ReadAllBytes(path), bom: false)) is not PyDict parsed)
                    throw new FormatException("not a JSON object");
                rc = parsed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or PyError)
            {
                errors.Add($"launch receipt {PyPath.Name(path)}: not readable as JSON ({ex.Message})");
                continue;
            }
            string lid = $"{S(rc.Get("condition"))}-{S(rc.Get("replicate"))}";
            if (!Ev.Eq(rc.Get("schemaVersion"), LaunchSchema))
            {
                errors.Add($"schema-unsupported: launch {lid} schemaVersion {PyText.Repr(rc.Get("schemaVersion"))}");
                continue;
            }
            var bind = Ev.OrEmpty(rc.Get("binding"));
            if (!Ev.Eq(bind.Get("planId"), plan["planId"]) || !Ev.Eq(bind.Get("checkId"), cid) || !Ev.Eq(bind.Get("armId"), aid))
                errors.Add($"foreign-run: launch {lid} was launched for {PyText.Repr(bind.Get("planId"))}/{PyText.Repr(bind.Get("checkId"))}/"
                           + $"{PyText.Repr(bind.Get("armId"))}, not {S(plan["planId"])}/{checkId}/{armId}");
            var started = Iso(rc.Get("launchStartedUtc"));
            if (started == null || (planCreated != null && started.Value.Microseconds < planCreated.Value.Microseconds))
                errors.Add($"foreign-run: launch {lid} started {PyText.Repr(rc.Get("launchStartedUtc"))}, before this plan was frozen ({S(plan.Get("createdUtc"))})");
            if (PyText.Lower(PyValue.Truthy(rc.Get("flipPlanSha256")) ? S(rc["flipPlanSha256"]) : "") != planSha)
                errors.Add($"identity-mismatch: launch {lid} ran flip plan {Ev.Head(S(rc.Get("flipPlanSha256")), 12)}, the import supplies {planSha[..12]}");
            var rcCfg = Ev.OrEmpty(rc.Get("configuration"));
            fingerprints.Add(PyText.Lower(PyValue.Truthy(rcCfg.Get("fingerprint")) ? S(rcCfg["fingerprint"]) : ""));
            string evSha = PyText.Lower(PyValue.Truthy(rc.Get("eventsSha256")) ? S(rc["eventsSha256"]) : "");
            if (!eventsBySha.TryGetValue(evSha, out var evPath))
            {
                errors.Add($"output-missing: launch {lid}'s events file (sha256 {Ev.Head(S(rc.Get("eventsSha256")), 12)}) is not among the outputs");
                continue;
            }
            try { launches.Add((rc, ReadEvents(evPath))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or PyError)
            {
                errors.Add($"launch {lid}: events not readable ({ex.Message})");
            }
        }
        var docCfg = Ev.OrEmpty(doc.Get("configuration"));
        string cfgFp = PyText.Lower(PyValue.Truthy(docCfg.Get("fingerprint")) ? S(docCfg["fingerprint"]) : "");
        if (fingerprints.Count > 0 && !(fingerprints.Count == 1 && fingerprints.Contains(cfgFp)))
            errors.Add("identity-mismatch: the launches' configuration fingerprints "
                       + PyText.Repr(PyOrder.SortedByCodePoint(fingerprints.Select(x => Ev.Head(x, 12))).Cast<object?>().ToList())
                       + $" are not the import's {Ev.Head(cfgFp, 12)}");

        var (reErrors, readings) = RereadLaunches(launches);
        errors.AddRange(reErrors);
        PyDict? derived = launches.Count > 0 ? DeriveResult(readings) : null;
        var res = Ev.OrEmpty(doc.Get("result"));
        if (derived != null && reErrors.Count == 0
            && (!Ev.Eq(res.Get("detector"), derived["detector"]) || !Ev.Eq(res.Get("findings"), derived["findings"])))
            errors.Add($"the import states {S(res.Get("detector"))}/{S(res.Get("findings"))}; its launches re-read as "
                       + $"{S(derived["detector"])}/{S(derived["findings"])}");

        // ---- identity: the validator's own rules, against the arm's frozen request
        var limits = new List<object?>();
        PyDict? identity = doc.Get("configuration") is PyDict
            ? Validator.EmitIdentity(new PyDict { ["configuration"] = doc.Get("configuration") }) : null;
        if (identity == null) errors.Add("identity-missing: configuration is absent");
        else
        {
            if (!Ev.Eq(doc.Get("state"), identity.Get("state")))
                errors.Add($"population-mismatch: import state {PyText.Repr(doc.Get("state"))} differs from configuration state {PyText.Repr(identity.Get("state"))}");
            foreach (var row in Ev.Items(identity.Get("plugins")).Select(PyValue.Dict))
            {
                object? want = row.Get("sha256");
                string path = PyValue.Truthy(row.Get("path")) ? PyPath.Str(S(row["path"])) : "";
                if (!PyValue.Truthy(row.Get("path")) || !File.Exists(path) || !PlanArms.IsSha256(want))
                    errors.Add($"identity-missing: plugin {S(row.Get("name"))} must name an existing path and 64-hex sha256");
                else if (Hashing.Sha256File(path) != PyText.Lower((string)want!))
                    errors.Add($"identity-mismatch: plugin {S(row.Get("name"))} does not hash to its stated sha256");
            }
            var validator = new Validator(run, plan);
            foreach (var x in validator.IdentityChecks(identity, arm, doc, Ev.OrEmpty(check.Get("adapter")), null, limits, null).Select(PyValue.Dict))
                errors.Add($"{S(x["cause"])}: {S(x["detail"])}");
            var req = Ev.OrEmpty(arm.Get("requested"));
            string expected = Validator.Fingerprint(Ev.Items(req.Get("plugins")), req.Get("providers"), req.Get("overlays"), req.Get("population"), identity.Get("state"));
            if (!Ev.Eq(identity.Get("fingerprint"), expected))
                errors.Add($"identity-mismatch: imported fingerprint {Ev.Head(S(identity.Get("fingerprint")), 12)} does not equal this arm's frozen request {expected[..12]}");
        }
        foreach (var lim in Ev.Items(docCfg.Get("limits"))) limits.Add(new PyDict { ["detail"] = S(lim) });

        if (Prerequisites.Satisfied(ledger, check, aid) is { } pre)
            errors.Add($"blocked-prerequisite: {Prerequisites.BlockDetail(ledger, pre, aid)}. Settle {pre} on this arm, then import again");
        // ...and the app scan's, for an overlay row: its appPrerequisites (the switch census) are app checks
        else if (RecordImport.AppGate(run, plan, check, aid) is { } appBlock)
            errors.Add(appBlock);
        if (errors.Count > 0) return Refuse("import-results refused: " + string.Join("; ", errors));

        // ---- copy the evidence beside the run, then write the receipt
        var sources = Roles.SelectMany(r => files[r].Select(f => f.path)).ToList();
        string imports = PyPath.Join(run, "imports");
        string dest = PyPath.Join(imports, key);
        for (int n = 2; Ev.Exists(dest); n++) dest = PyPath.Join(imports, $"{key}~{n}");
        Directory.CreateDirectory(imports);
        string temp = PyPath.Join(imports, $"{key}-{Path.GetRandomFileName().Replace(".", "")}");
        Directory.CreateDirectory(temp);
        var names = sources.Select((p, i) => $"{i + 1:00}-{PyPath.Name(p)}").ToList();
        try
        {
            File.WriteAllBytes(PyPath.Join(temp, "result.json"), manifestBytes);
            for (int i = 0; i < sources.Count; i++) ImportResults.StoreFile(sources[i], PyPath.Join(temp, names[i]), link: false);
            if (!names.Select(n => Hashing.Sha256File(PyPath.Join(temp, n))).SequenceEqual(sources.Select(Hashing.Sha256File)))
                return Refuse("output-stale: imported evidence changed while it was being copied");
            Directory.Move(temp, dest);
        }
        finally
        {
            if (Directory.Exists(temp))
                try { Directory.Delete(temp, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var copied = new List<string> { PyPath.Join(dest, "result.json") };
        copied.AddRange(names.Select(n => PyPath.Join(dest, n)));
        outputs?.Invoke(copied.Count);
        var receipt = Receipts.WriteReceipt(run, plan, check, arm, null,
            new PyDict { ["kind"] = "live-import", ["sourceKind"] = "live-import" }, null, runId, null,
            started: (string?)captured, ended: EvalStore.UtcNow(), exitCode: 0L, outputs: copied, log: null,
            consumed: new PyDict
            {
                ["candidateSha256"] = arm.Get("candidateSha256"), ["candidatePath"] = arm.Get("candidatePath"),
                ["configuration"] = identity, ["configurationSource"] = "live-driver",
                ["registrySha256"] = PyValue.Dict(plan["registry"])["sha256"], ["tool"] = tool,
                ["optionsSha256"] = Hashing.Sha256Text(PyJson.Canonical(plan["options"])),
                ["scopeSha256"] = PyValue.Dict(plan["scope"])["sha256"], ["programs"] = programs,
            },
            validation: new PyDict { ["accepted"] = true, ["rejections"] = new List<object?>(), ["identityLimits"] = limits },
            result: new PyDict
            {
                ["execution"] = "completed", ["detector"] = derived!["detector"], ["findings"] = derived["findings"],
                ["coverage"] = derived["coverage"], ["strength"] = Ev.OrEmpty(check.Get("evidence")).Get("strength"),
                ["verdict"] = null, ["readings"] = derived["readings"],
                ["note"] = "re-read from the launches' events by the switch-flip reading rule; a completion is no failure under the listed conditions only",
            },
            extra: new PyDict
            {
                ["imported"] = new PyDict
                {
                    ["sourceSha256"] = Hashing.Sha256File(copied[0]), ["capturedUtc"] = captured, ["tool"] = tool, ["origin"] = "fresh",
                    ["bindingRecordedAtLaunch"] = true, ["flipPlanSha256"] = planSha, ["launches"] = (long)launches.Count,
                },
            });
        ledger.Transition(checkId, armId, null, "completed", receiptId: (string?)receipt["receiptId"], detector: (string?)derived["detector"],
            extra: new PyDict { ["imported"] = true, ["importOrigin"] = "fresh" });
        imported?.Invoke(copied[0]);
        Console.Out.WriteLine($"imported {checkId} [{armId}]: {S(derived["detector"])}, {S(derived["findings"])} condition(s) with a finding, "
                              + $"{launches.Count} launch(es) re-read");
        return Exit.Ok;
    }

    // Path(p).parent as text
    static string Parent(string path)
    {
        string? dir = Path.GetDirectoryName(PyPath.Str(path));
        return string.IsNullOrEmpty(dir) ? "." : dir;
    }
}
