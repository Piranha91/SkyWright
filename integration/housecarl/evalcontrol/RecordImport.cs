using System.Numerics;
using HousecarlCore.Progress;
using RunControl;

namespace EvalControl;

/// <summary>`evalcontrol import --run AI --file DOC`: binds evidence a record reader already produced to one arm the
/// overlay plan left open (an mcp-only row: a record check only houseCARL's tools read), and writes a normal receipt
/// for it (moved from runcontrol's import-results with the record checks; evalrun.py's cmd_import_results). The shared
/// start (the document bound to the plan, the check and the arm, under the controller's identity) is runcontrol's
/// ImportResults.Open; the rest is the record import's own rule. Besides the overlay row's own prerequisites, its
/// appPrerequisites are asked of the app scan's ledger (the app bundle the overlay is bound to).
/// Exit 0, or 2 with one REFUSED line that joins every reason.</summary>
public static class RecordImport
{
    static readonly string[] Detectors = ["finding", "no-finding"];

    // One refusal: the sentence on standard error, exit 2.
    static int Refuse(string message)
    {
        Console.Error.WriteLine($"REFUSED: {message}");
        return Exit.Refused;
    }

    // d.get(a, d.get(b)): the first key when it is there, whatever it holds.
    static object? Either(PyDict d, string a, string b) => d.ContainsKey(a) ? d[a] : d.Get(b);

    static bool Count(object? v) => PyValue.IsInt(v) && PyValue.Int(v) >= 0;

    /// <summary>A judged-0 import is "nothing to judge" only when the importer states Members: 0 on every coverage
    /// line and the arm loads none of the plan's target plugins. The sentence recorded on acceptance, or null and the
    /// rest of the refusal.</summary>
    public static (string? sentence, string whyNot) EmptyPopulation(PyDict plan, PyDict arm, IReadOnlyList<BigInteger?> members, BigInteger unjudgeable)
    {
        var targets = Ev.Items(plan.Get("targets")).OfType<PyDict>().Where(t => PyValue.Truthy(t.Get("name"))).Select(t => Ev.Str(t["name"])).ToList();
        var loads = GenericRows.ArmRequestedPlugins(arm);
        var loaded = targets.Where(t => loads.Contains(PyText.Lower(t))).ToList();
        string names = string.Join(", ", targets.Select(t => PyText.Repr(t)));
        const string recapture = "re-capture this arm's evidence and import it again";
        if (unjudgeable > 0 || members.Any(m => m is { } n && n > 0))
            return (null, "so members of the population were found and none was judged, which is a failed extraction; " + recapture);
        if (targets.Count == 0)
            return (null, "and this plan names no target plugin, so nothing shows the arm holds none of the check's population; " + recapture);
        if (loaded.Count > 0)
            return (null, $"and this arm loads the plan's target {string.Join(", ", loaded.Select(t => PyText.Repr(t)))}, whose records are the "
                          + "check's population, so 0 judged is a capture that read nothing; " + recapture);
        if (members.Count == 0 || members.Any(m => m == null))
            return (null, $"which cannot be told from a capture that read nothing; this arm loads none of the plan's target "
                          + $"plugins ({names}), so if the capture is right and this arm holds none of the check's "
                          + "population, state Members: 0 on each coverage line and import again, otherwise " + recapture);
        return ($"nothing to judge: the import states that its population held no member on this arm, and this arm loads "
                + $"none of the plan's target plugins ({names}); recorded as not applicable on this arm.", "");
    }

    /// <summary>An import's rows output: the conflict-file shape, every pair naming plugins of the arm it is imported for.</summary>
    public static List<string> RowsOutputErrors(string path, PyDict? arm)
    {
        if (PyJson.LoadEvalFile(path) is not PyDict doc || doc.Get("Conflicts") is not List<object?> conflicts)
            return [$"rows-invalid: {PyPath.Name(path)} is not a conflict file (an object with a Conflicts list)"];
        var names = Ev.Items(Ev.OrEmpty(arm?.Get("requested")).Get("plugins")).Select(PyValue.Dict)
            .Select(x => PyText.Lower(PyValue.Truthy(x.Get("name")) ? Ev.Str(x["name"]) : "")).ToHashSet(StringComparer.Ordinal);
        var foreign = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in conflicts.OfType<PyDict>())
            foreach (string k in new[] { "A", "B" })
                if (PyValue.Truthy(c.Get(k)) && !names.Contains(PyText.Lower(Ev.Str(c[k])))) foreign.Add(Ev.Str(c[k]));
        if (foreign.Count > 0)
            return [$"rows-foreign: {PyPath.Name(path)} names " + string.Join(", ", PyOrder.SortedByCodePoint(foreign)) + ", which is not a plugin of arm "
                    + Ev.Str(arm?.Get("armId"))];
        return [];
    }

    // Path(p).parent as text.
    static string Parent(string path)
    {
        string? dir = Path.GetDirectoryName(PyPath.Str(path));
        return string.IsNullOrEmpty(dir) ? "." : dir;
    }

    /// <summary>The command, under its own run-log record. The run is an overlay (an AI-side evaluation's), or any run
    /// folder whose plan leaves an mcp-only row open (SkyWright's own registry has none, so an app scan's plan never
    /// does; a test's registry may).</summary>
    public static int Run(string runFolder, string file)
    {
        string run = PyPath.Resolve(runFolder);
        EvalStore.SetRunStore(run);
        using var record = RunLog.Begin("evalcontrol-import", new RunOptions { StageSource = StageSource.Counted });
        record.Stage("validate-and-import", total: 1, unit: "results");
        int rc;
        try { rc = Import(run, file, record.RunId, n => record.Feature("outputs", n, "files"), path => { record.Artifact(path); record.Advance(); }); }
        catch (Exception ex)
        {
            record.Fail($"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
        if (rc == Exit.Refused) record.Refuse("import validation refused; the diagnostic above names the cause", Exit.Refused);
        else if (rc != Exit.Ok) record.Fail("import failed", rc);
        return rc;
    }

    /// <summary>The app scan's prerequisite gate for an overlay row: each of its appPrerequisites settled on this arm
    /// (or on the arm that answers it) in the bound app bundle's ledger, by runcontrol's own rule. Null when it holds,
    /// or when the plan is not an overlay or the row names none; else the refusal's clause.</summary>
    public static string? AppGate(string run, PyDict plan, PyDict check, object? armId)
    {
        if (plan.Get(OverlayPlan.Key) is not PyDict bound || !PyValue.Truthy(check.Get("appPrerequisites"))) return null;
        string app = PyPath.Resolve(PyPath.Join(run, Ev.Str(bound.Get("appBundle"))));
        if (PyJson.LoadEvalFile(PyPath.Join(app, "plan.json")) is not PyDict appPlan || !Ev.Exists(PyPath.Join(app, "ledger.json")))
            return $"blocked-prerequisite: the app bundle this overlay is bound to is not readable at {app}, so its "
                   + $"{string.Join(", ", Ev.Items(check["appPrerequisites"]).Select(Ev.Str))} cannot be read; plan the overlay again over the app scan's bundle";
        var appLedger = new Ledger(app, appPlan);
        var gate = new PyDict { ["checkId"] = check.Get("checkId"), ["prerequisites"] = check["appPrerequisites"] };
        if (Prerequisites.Satisfied(appLedger, gate, armId) is not { } pre) return null;
        return $"blocked-prerequisite: {Prerequisites.BlockDetail(appLedger, pre, armId)} in the app scan. An import is credited only "
               + $"where the check could run; settle {pre} on this arm in the app scan (runcontrol measures it), bundle it and plan the "
               + "overlay again, then import again";
    }

    /// <summary>The import itself, under the run-log record `runId`; `outputs` and `imported` report to it.</summary>
    public static int Import(string runFolder, string file, string? runId, Action<int>? outputs = null, Action<string>? imported = null)
    {
        if (ImportResults.Open(runFolder, file) is not { } o) return Exit.Refused;
        string run = o.Run, src = o.Src;
        var (plan, ledger, doc, manifestBytes, programs, check, arm) = (o.Plan, o.Ledger, o.Doc, o.ManifestBytes, o.Programs, o.Check, o.Arm);
        string manifestSha = Hashing.Sha256Bytes(manifestBytes);
        object? cid = doc.Get("checkId"), aid = doc.Get("armId");
        var adapter = Ev.OrEmpty(check.Get("adapter"));
        // a live game driver's receipts: their own validator re-reads every launch
        if (adapter.Get("kind") is "live-import")
            return LiveImport.Import(run, src, plan, ledger, check, arm, doc, manifestBytes, programs, runId, outputs, imported);
        if (adapter.Get("kind") is not "mcp-only")
            return Refuse($"wrong-check: {Ev.Str(cid)} is {PyText.Repr(adapter.Get("kind"))}, not mcp-only; "
                          + "import-results cannot replace a runnable or unsupported detector");
        var planned = Ev.Items(check.Get("arms")).Select(PyValue.Dict).ToList();
        if (arm == null || !planned.Any(x => Ev.Eq(x.Get("armId"), aid)))
            return Refuse($"identity-mismatch: check {Ev.Str(cid)} has no planned arm {PyText.Repr(aid)}");
        // the row the plan left open for an import is the one it recorded mcp-only; any other is answered already
        object? rowStatus = planned.FirstOrDefault(x => Ev.Eq(x.Get("armId"), aid))?.Get("status");
        if (rowStatus is not "mcp-only")
            return Refuse($"wrong-check: the plan records {Ev.Str(cid)} as {PyText.Repr(rowStatus)} on arm {Ev.Str(aid)}, not mcp-only, so it is "
                          + "not waiting for imported evidence"
                          + (Ev.Str(rowStatus).StartsWith("not-applicable", StringComparison.Ordinal) && PyValue.Truthy(check.Get("applicabilityEvidence"))
                              ? $": {Ev.Str(check["applicabilityEvidence"])}" : "")
                          + ". An import completes a check the plan left open; it never overrides the plan's answer");
        string checkId = Ev.Str(cid), armId = Ev.Str(aid);
        string key = Ledger.Key(cid, aid, null);
        var prior = Ev.OrEmpty(ledger.Entry(cid, aid, null));
        if (EvalStore.IsTerminalOk(prior.Get("status")) || (prior.Get("status") is "not-applicable" && PyValue.Truthy(prior.Get("imported"))))
            return Refuse($"duplicate-receipt: {key} already has accepted evidence");

        object? captured = doc.Get("capturedUtc"), tool = doc.Get("tool");
        string? nothingToJudge = null;
        PyDict? identity;
        try { identity = Validator.EmitIdentity(new PyDict { ["configuration"] = doc.Get("configuration") }); }
        catch (Exception ex) when (ex is PyError or InvalidCastException or NullReferenceException) { identity = null; }
        object? resultRaw = doc.Get("result"), rows = doc.Get("outputs");
        var errors = new List<string>();
        PyDateTime.Stamp? capturedTime = null;
        if (!PyValue.Truthy(captured) || tool is not PyDict toolDoc || !PyValue.Truthy(toolDoc.Get("name")))
            errors.Add("the import must name capturedUtc and tool.name");
        else if (captured is string capturedText && PyDateTime.FromIso(capturedText.Replace("Z", "+00:00")) is { Aware: true } stamp)
            capturedTime = stamp;
        else
            errors.Add("capturedUtc must be an ISO-8601 timestamp with a timezone");
        if (identity == null) errors.Add("identity-missing: configuration is absent");
        if (resultRaw is not PyDict result)
        {
            errors.Add("result is absent");
            result = new PyDict();
        }
        object? detector = result.Get("detector"), findings = result.Get("findings"), coverage = result.Get("coverage");
        if (detector is not string detectorWord || !Detectors.Contains(detectorWord))
            errors.Add($"result.detector must be one of {string.Join(", ", Detectors)}");
        if (!Count(findings))
            errors.Add("result.findings must be a non-negative integer");
        else if ((detector is "finding") != (PyValue.Int(findings) > 0))
            errors.Add("result.detector contradicts result.findings");
        if (coverage is not List<object?> lines || lines.Count == 0)
            errors.Add("coverage-missing: result.coverage must contain at least one population");
        else
        {
            BigInteger judged = 0, unjudgeable = 0;
            var members = new List<BigInteger?>();   // each line's stated member count; null where the line states none
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i] is not PyDict line)
                {
                    errors.Add($"coverage-invalid: result.coverage[{i}] must be an object");
                    continue;
                }
                if (Either(line, "Population", "population") is not string population || PyValue.Strip(population).Length == 0)
                    errors.Add($"coverage-invalid: result.coverage[{i}].Population must be a non-empty string");
                object? count = Either(line, "Judged", "judged");
                object? reasons = line.ContainsKey("Unjudgeable") ? line["Unjudgeable"] : line.ContainsKey("unjudgeable") ? line["unjudgeable"] : new PyDict();
                if (!Count(count)) errors.Add($"coverage-invalid: result.coverage[{i}].Judged must be a non-negative integer");
                else judged += PyValue.Int(count);
                if (reasons is not PyDict reasonCounts || reasonCounts.Any(kv => !Count(kv.Value)))
                    errors.Add($"coverage-invalid: result.coverage[{i}].Unjudgeable values must be non-negative integers");
                else
                    foreach (var (_, v) in reasonCounts) unjudgeable += PyValue.Int(v);
                object? m = Either(line, "Members", "members");
                if (m != null && !Count(m))
                {
                    errors.Add($"coverage-invalid: result.coverage[{i}].Members must be a non-negative integer");
                    m = null;
                }
                members.Add(m == null ? null : PyValue.Int(m));
            }
            if (errors.Count == 0 && judged <= 0)
            {
                (nothingToJudge, string whyNot) = EmptyPopulation(plan, arm, members, unjudgeable);
                if (nothingToJudge == null)
                    errors.Add($"extraction-failed: imported evidence judged {judged} with {unjudgeable} unjudgeable, {whyNot}");
            }
        }
        var rowList = rows as List<object?>;
        var roles = rowList == null ? [] : PyOrder.SortedByCodePoint(rowList.OfType<PyDict>().Select(x => Ev.Str(x.Get("role"))));
        if (rowList == null || rowList.Count is not (2 or 3))
            errors.Add("output-missing: outputs must include the raw MCP request and response files, and may add rows");
        else if (!roles.SequenceEqual(["request", "response"]) && !roles.SequenceEqual(["request", "response", "rows"]))
            errors.Add("output-missing: outputs must contain the roles request and response, and at most one rows");

        var sources = new List<string>();
        (string path, string sha256)? request = null;
        if (rowList != null)
            for (int i = 0; i < rowList.Count; i++)
            {
                if (rowList[i] is not PyDict row || !PyValue.Truthy(row.Get("path")) || !PlanArms.IsSha256(row.Get("sha256")))
                {
                    errors.Add($"output-missing: outputs[{i}] must name path and 64-hex sha256");
                    continue;
                }
                if (row["path"] is not string stated || PyValue.Strip(stated).Length == 0)
                {
                    errors.Add($"output-missing: outputs[{i}].path must be a non-empty string");
                    continue;
                }
                string path = PyPath.Str(stated);
                if (!Ev.IsAbsolute(path)) path = PyPath.Join(Parent(src), stated);
                string want = PyText.Lower((string)row["sha256"]!);
                if (!File.Exists(path)) errors.Add($"output-missing: {path}");
                else if (Hashing.Sha256File(path) != want) errors.Add($"output-stale: {path} does not hash to the imported sha256");
                else if (Bundle.BinarySuffixes.Contains(PyText.Lower(PyPath.Suffix(path))))
                    errors.Add($"output-missing: raw evidence {PyPath.Name(path)} is a binary excluded from bundles");
                else
                {
                    sources.Add(path);
                    if (row.Get("role") is "request") request = (path, want);
                    if (row.Get("role") is "rows") errors.AddRange(RowsOutputErrors(path, arm));
                }
            }

        // where the evidence came from, checked on the hash-checked request bytes, before the identity checks: they
        // relax one rule only for a reuse this has already accepted
        var (originErrors, originFields) = ImportOrigin.Refusals(doc, plan, aid, capturedTime, request);
        errors.AddRange(originErrors);
        PyDict? declaredReuse = originFields.Get("origin") is "reused" ? originFields : null;

        var limits = new List<object?>();
        if (identity != null)
        {
            if (!Ev.Eq(doc.Get("state"), identity.Get("state")))
                errors.Add($"population-mismatch: import state {PyText.Repr(doc.Get("state"))} differs from "
                           + $"configuration state {PyText.Repr(identity.Get("state"))}");
            foreach (var (kind, values) in new[] { ("plugin", identity.Get("plugins")), ("overlay", identity.Get("overlays")) })
                foreach (var row in Ev.Items(values).Select(PyValue.Dict))
                {
                    object? want = row.Get("sha256");
                    string named = Ev.Str(PyValue.Truthy(row.Get("name")) ? row["name"] : row.Get("kind"));
                    string path = PyValue.Truthy(row.Get("path")) ? PyPath.Str(Ev.Str(row["path"])) : "";
                    if (!PyValue.Truthy(row.Get("path")) || !File.Exists(path) || !PlanArms.IsSha256(want))
                        errors.Add($"identity-missing: {kind} {named} must name an existing path and 64-hex sha256");
                    else if (Hashing.Sha256File(path) != PyText.Lower((string)want!))
                        errors.Add($"identity-mismatch: {kind} {named} does not hash to its stated sha256");
                }
            var validator = new Validator(run, plan);
            foreach (var x in validator.IdentityChecks(identity, arm, doc, adapter, null, limits, declaredReuse).Select(PyValue.Dict))
                errors.Add($"{Ev.Str(x["cause"])}: {Ev.Str(x["detail"])}");
            var req = Ev.OrEmpty(arm.Get("requested"));
            string expected = Validator.Fingerprint(Ev.Items(req.Get("plugins")), req.Get("providers"), req.Get("overlays"), req.Get("population"), identity.Get("state"));
            if (!Ev.Eq(identity.Get("fingerprint"), expected))
                errors.Add($"identity-mismatch: imported fingerprint {Ev.Head(Ev.Str(identity.Get("fingerprint")), 12)} does not equal this arm's frozen "
                           + $"request {expected[..12]}");
        }

        // the controller's own prerequisite gate: evidence for a check that could not have run on this arm is not credited
        if (Prerequisites.Satisfied(ledger, check, aid) is { } pre)
            errors.Add($"blocked-prerequisite: {Prerequisites.BlockDetail(ledger, pre, aid)}. An import is credited only "
                       + $"where the check could run; settle {pre} on this arm (`run` or `resume --run RUN` measures "
                       + "it; an arm whose order is refused must be fixed and planned again), then import again");
        // ...and the app scan's, for an overlay row: its appPrerequisites are app checks, settled (or not) in the app
        // bundle this overlay is bound to
        else if (AppGate(run, plan, check, aid) is { } appBlock)
            errors.Add(appBlock);
        if (errors.Count > 0) return Refuse("import-results refused: " + string.Join("; ", errors));

        // a folder left by an import that is no longer credited backs no current receipt: it is kept, and a re-import
        // is copied beside it
        string imports = PyPath.Join(run, "imports");
        string dest = PyPath.Join(imports, key);
        for (int n = 2; Ev.Exists(dest); n++) dest = PyPath.Join(imports, $"{key}~{n}");
        Directory.CreateDirectory(imports);
        string temp = PyPath.Join(imports, $"{key}-{Path.GetRandomFileName().Replace(".", "")}");
        Directory.CreateDirectory(temp);
        var names = new List<string>();
        try
        {
            File.WriteAllBytes(PyPath.Join(temp, "result.json"), manifestBytes);
            for (int i = 0; i < sources.Count; i++)
            {
                var row = (PyDict)rowList![i]!;
                names.Add(row.Get("role") is "rows" ? $"{i + 1:00}-rows.json" : $"{i + 1:00}-{PyPath.Name(sources[i])}");
            }
            // the raw transcripts are kept once per run, linked to the capture's own file where the volume allows; the
            // rows file is copied, since its writer rewrites it in place
            for (int i = 0; i < sources.Count; i++)
                ImportResults.StoreFile(sources[i], PyPath.Join(temp, names[i]), link: ((PyDict)rowList![i]!).Get("role") is "request" or "response");
            var copiedHashes = names.Select(n => Hashing.Sha256File(PyPath.Join(temp, n)));
            var approved = rowList!.Select(r => PyText.Lower((string)((PyDict)r!)["sha256"]!));
            if (Hashing.Sha256File(PyPath.Join(temp, "result.json")) != manifestSha || !copiedHashes.SequenceEqual(approved))
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
        string manifestCopy = copied[0];

        outputs?.Invoke(copied.Count);
        object? detectorOut = nothingToJudge != null ? null : detector;
        var receipt = Receipts.WriteReceipt(run, plan, check, arm, null,
            new PyDict { ["kind"] = "mcp-import", ["sourceKind"] = "mcp-only" }, null, runId, null,
            started: (string?)captured, ended: EvalStore.UtcNow(), exitCode: 0L, outputs: copied, log: null,
            consumed: new PyDict
            {
                ["candidateSha256"] = arm.Get("candidateSha256"),
                ["candidatePath"] = arm.Get("candidatePath"),
                ["configuration"] = identity, ["configurationSource"] = "emit",
                ["registrySha256"] = PyValue.Dict(plan["registry"])["sha256"], ["tool"] = tool,
                ["optionsSha256"] = Hashing.Sha256Text(PyJson.Canonical(plan["options"])),
                ["scopeSha256"] = PyValue.Dict(plan["scope"])["sha256"], ["programs"] = programs,
            },
            validation: new PyDict { ["accepted"] = true, ["rejections"] = new List<object?>(), ["identityLimits"] = limits },
            result: new PyDict
            {
                ["execution"] = nothingToJudge != null ? "not-applicable" : "completed",
                ["detector"] = detectorOut, ["findings"] = findings,
                ["coverage"] = coverage, ["strength"] = Ev.OrEmpty(check.Get("evidence")).Get("strength"),
                ["verdict"] = result.Get("verdict"), ["note"] = nothingToJudge ?? result.Get("note"),
            },
            extra: new PyDict
            {
                ["imported"] = new PyDict { ["sourceSha256"] = Hashing.Sha256File(manifestCopy), ["capturedUtc"] = captured, ["tool"] = tool }.Update(originFields),
            });
        string originWord = Ev.Str(originFields["origin"]);
        ledger.Transition(checkId, armId, null, nothingToJudge != null ? "not-applicable" : "completed",
            receiptId: (string?)receipt["receiptId"], detector: (string?)detectorOut, detail: nothingToJudge,
            extra: new PyDict { ["imported"] = true, ["importOrigin"] = originWord });
        imported?.Invoke(manifestCopy);
        if (nothingToJudge != null)
        {
            Console.Out.WriteLine($"imported {checkId} [{armId}]: not-applicable - {nothingToJudge}");
            return Exit.Ok;
        }
        string source = "";
        if (originWord == "reused")
        {
            var from = PyValue.Dict(originFields["reusedFrom"]);
            source = $" (declared source: plan {Ev.Str(from["planId"])} arm {Ev.Str(from["armId"])}, not verified)";
        }
        Console.Out.WriteLine($"imported {checkId} [{armId}]: {Ev.Str(detector)}, {Ev.Str(findings)} finding(s), "
                              + $"{copied.Count} evidence file(s), origin {originWord}{source}");
        return Exit.Ok;
    }
}
