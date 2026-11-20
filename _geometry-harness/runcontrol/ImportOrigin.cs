namespace RunControl;

/// <summary>Where an import's evidence came from (evalrun.py's origin_refusals and what it reads): a capture made for
/// this plan's arm, shown by the capture token in every MCP initialize request, or a declared reuse.</summary>
public static class ImportOrigin
{
    /// <summary>What a capture made for a plan's arm puts in every MCP initialize request's clientInfo.name.</summary>
    public static string CaptureToken(string planId, string armId) => $"evalrun:{planId}:{armId}";

    /// <summary>bytes.decode("utf-8-sig"): the text, or Python's reason and the byte it starts at (after any mark).</summary>
    public static (string? text, string? reason, int start) DecodeUtf8Sig(byte[] all)
    {
        int skip = all.Length >= 3 && all[0] == 0xEF && all[1] == 0xBB && all[2] == 0xBF ? 3 : 0;
        var b = all.AsSpan(skip);
        static bool Cont(byte x) => (x & 0xC0) == 0x80;
        int i = 0, n = b.Length;
        while (i < n)
        {
            byte ch = b[i];
            int left = n - i;
            if (ch < 0x80) { i++; continue; }
            if (ch < 0xE0)
            {
                if (ch < 0xC2) return (null, "invalid start byte", i);
                if (left < 2) return (null, "unexpected end of data", i);
                if (!Cont(b[i + 1])) return (null, "invalid continuation byte", i);
                i += 2;
                continue;
            }
            if (ch < 0xF0)
            {
                if (left < 2) return (null, "unexpected end of data", i);
                byte c2 = b[i + 1];
                if (!Cont(c2) || (c2 < 0xA0 ? ch == 0xE0 : ch == 0xED)) return (null, "invalid continuation byte", i);
                if (left < 3) return (null, "unexpected end of data", i);
                if (!Cont(b[i + 2])) return (null, "invalid continuation byte", i);
                i += 3;
                continue;
            }
            if (ch < 0xF5)
            {
                if (left < 2) return (null, "unexpected end of data", i);
                byte c2 = b[i + 1];
                if (!Cont(c2) || (c2 < 0x90 ? ch == 0xF0 : ch == 0xF4)) return (null, "invalid continuation byte", i);
                if (left < 3) return (null, "unexpected end of data", i);
                if (!Cont(b[i + 2])) return (null, "invalid continuation byte", i);
                if (left < 4) return (null, "unexpected end of data", i);
                if (!Cont(b[i + 3])) return (null, "invalid continuation byte", i);
                i += 4;
                continue;
            }
            return (null, "invalid start byte", i);
        }
        return (new System.Text.UTF8Encoding(false, true).GetString(b), null, 0);
    }

    /// <summary>The MCP initialize requests in a raw request file (JSON Lines; an array is a batch), or null and why
    /// the file cannot be read as requests. Lines end at a line feed only.</summary>
    public static (List<PyDict>? found, string? why) InitializeRequests(byte[] data)
    {
        var (text, reason, start) = DecodeUtf8Sig(data);
        if (text == null) return (null, $"the request file is not UTF-8 ({reason} at byte {start})");
        var found = new List<PyDict>();
        int n = 0;
        foreach (string raw in text.Split('\n'))
        {
            n++;
            string line = raw.TrimEnd('\r');
            if (PyValue.Strip(line).Length == 0) continue;
            object? value;
            try { value = PyJson.Loads(line); }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return (null, $"line {n} of the request file is not JSON, so its requests cannot be read");
            }
            foreach (var msg in value is List<object?> batch ? batch : [value])
                if (msg is PyDict d && d.Get("method") is string m && m == "initialize") found.Add(d);
        }
        return (found, null);
    }

    /// <summary>params.clientInfo.name of one initialize request, or null when a level is absent or not an object.</summary>
    public static object? ClientName(PyDict msg) => ((msg.Get("params") as PyDict)?.Get("clientInfo") as PyDict)?.Get("name");

    // A non-empty string after strip().
    static bool Named(object? v) => v is string s && PyValue.Strip(s).Length > 0;

    // What two arms' frozen requests are compared by: plugins in order, providers in order, overlays sorted, population.
    static List<object?> Frozen(PyDict? arm)
    {
        var r = Ev.OrEmpty(arm?.Get("requested"));
        static string Text(object? v) => PyValue.Truthy(v) ? Ev.Str(v) : "";
        var overlays = Ev.Items(r.Get("overlays")).Select(PyValue.Dict)
            .Select(o => (kind: Text(o.Get("kind")), sha: PyText.Lower(Text(o.Get("sha256")))))
            .OrderBy(x => x.kind, PyOrder.CodePoint).ThenBy(x => x.sha, PyOrder.CodePoint)
            .Select(x => (object?)new List<object?> { x.kind, x.sha }).ToList();
        return
        [
            Ev.Items(r.Get("plugins")).Select(PyValue.Dict).Select(p => (object?)new List<object?> { PyText.Lower(Text(p.Get("name"))), PyText.Lower(Text(p.Get("sha256"))) }).ToList(),
            Ev.Items(r.Get("providers")).Select(PyValue.Dict).Select(v => (object?)new List<object?> { Text(v.Get("kind")), PyText.Lower(Text(v.Get("name"))) }).ToList(),
            overlays,
            r.Get("population"),
        ];
    }

    /// <summary>The origin checks for one import document: (refusal sentences, the receipt's origin fields).
    /// `request` is the hash-checked request file and its stated hash; `captured` the parsed capturedUtc.</summary>
    public static (List<string> errors, PyDict fields) Refusals(PyDict doc, PyDict plan, object? armId, PyDateTime.Stamp? captured,
        (string path, string sha256)? request)
    {
        var errors = new List<string>();
        object? origin = doc.Get("origin");
        string token = CaptureToken(Ev.Str(plan["planId"]), Ev.Str(armId));
        string how = $"declare origin fresh for evidence captured for this plan's arm (every MCP initialize request carries "
                     + $"clientInfo.name \"{token}\" and capturedUtc is not before the plan), or origin reused with reusedFrom "
                     + "{planId, armId, resultSha256} naming the evaluation it was captured for";
        if (!doc.ContainsKey("origin"))
            return ([$"origin-missing: the import does not say where its evidence came from; {how}. An envelope written "
                     + "before origin was required takes one of those two paths"], new PyDict());
        if (origin is not ("fresh" or "reused"))
            return ([$"origin-invalid: origin must be fresh or reused, found {PyText.Repr(origin)}; {how}"], new PyDict());
        if (origin is "reused")
        {
            if (doc.Get("reusedFrom") is not PyDict src)
                return ([$"reusedFrom-missing: origin reused must name its source as reusedFrom {{planId, armId, "
                         + $"resultSha256}}, found {PyText.Repr(doc.Get("reusedFrom"))}"], new PyDict());
            var bad = new[] { "planId", "armId" }.Where(k => !Named(src.Get(k))).Select(k => $"reusedFrom.{k} must be a non-empty string").ToList();
            if (!PlanArms.IsSha256(src.Get("resultSha256")))
                bad.Add("reusedFrom.resultSha256 must be 64-hex: the sha256 of the source run's "
                        + "imports/<checkId>__<armId>/result.json, the import document that evaluation accepted");
            if (bad.Count > 0) return (["reusedFrom-invalid: " + string.Join("; ", bad)], new PyDict());
            if (Ev.Eq(src["planId"], plan["planId"]) && Ev.Eq(src["armId"], armId))
                return ([$"reusedFrom-invalid: reusedFrom names this plan's own arm {Ev.Str(armId)}; reuse means evidence "
                         + "captured for another setup or another evaluation, not for this one"], new PyDict());
            // reuse between two setups of this plan is a different statement from reuse across evaluations
            string within = Ev.Eq(src["planId"], plan["planId"]) ? "this-evaluation" : "another-evaluation";
            if (within == "this-evaluation")
            {
                // the one source this controller can check without reading another run: both frozen requests are in this plan
                var arms = Ev.Items(plan.Get("arms")).Select(PyValue.Dict).ToList();
                var named = arms.FirstOrDefault(a => Ev.Eq(a.Get("armId"), src["armId"]));
                if (named == null)
                {
                    string known = string.Join(", ", arms.Select(a => Ev.Str(a.Get("armId"))));
                    return ([$"reusedFrom-invalid: reusedFrom names setup {PyText.Repr(src["armId"])} of this plan, which has no "
                             + $"such arm; a within-evaluation source must be one of this plan's setups ({(known.Length > 0 ? known : "none")})"], new PyDict());
                }
                var here = arms.FirstOrDefault(a => Ev.Eq(a.Get("armId"), armId));
                if (here != null && !PyValue.Eq(Frozen(named), Frozen(here)))
                    return ([$"reusedFrom-invalid: reusedFrom names setup {PyText.Repr(src["armId"])} of this plan, whose FROZEN "
                             + "request is not this arm's: a reader of a within-evaluation reuse is told the source "
                             + "setup's tested configuration is the same, and here the plan itself says it is not. "
                             + "Import this evidence against the setup it was measured for, or as origin reused from "
                             + "the evaluation it really came from"], new PyDict());
            }
            return ([], new PyDict
            {
                ["origin"] = "reused", ["reusedWithin"] = within,
                ["reusedFrom"] = new PyDict
                {
                    ["planId"] = src["planId"], ["armId"] = src["armId"], ["resultSha256"] = PyText.Lower(Ev.Str(src["resultSha256"])),
                },
                ["reusedFromVerified"] = false,
                ["reusedFromNote"] = "declared by the importer's caller: the source run was not read, so "
                                     + "resultSha256 is a statement, not verified provenance",
            });
        }
        // fresh
        if (doc.ContainsKey("reusedFrom"))
            errors.Add("origin-invalid: origin fresh must not declare reusedFrom; evidence captured for another "
                       + "evaluation is imported as origin reused");
        long count = 0;
        if (request is not { } req)
            errors.Add($"origin-unverified: origin fresh requires the request output to be readable so every MCP "
                       + $"initialize request can be checked for clientInfo.name \"{token}\"");
        else
        {
            byte[] data = File.ReadAllBytes(req.path);
            if (Hashing.Sha256Bytes(data) != req.sha256)
                errors.Add($"output-stale: {req.path} changed while its requests were being read");
            else
            {
                var (inits, why) = InitializeRequests(data);
                if (inits == null)
                    errors.Add($"origin-unverified: {why}; origin fresh requires every MCP initialize request to "
                               + $"carry clientInfo.name \"{token}\"");
                else if (inits.Count == 0)
                    errors.Add("origin-unverified: the request file contains no MCP initialize request, so it "
                               + "cannot show a capture for this plan's arm; origin fresh requires every initialize "
                               + $"request to carry clientInfo.name \"{token}\"");
                else
                {
                    count = inits.Count;
                    var wrong = inits.Select(ClientName).Where(name => !(name is string s && s == token)).ToList();
                    if (wrong.Count > 0)
                        errors.Add($"origin-mismatch: {wrong.Count} of {inits.Count} MCP initialize request(s) do not "
                                   + $"carry clientInfo.name \"{token}\" (found "
                                   + string.Join(", ", PyOrder.SortedByCodePoint(wrong.Select(PyText.Repr).Distinct(StringComparer.Ordinal)).Take(4))
                                   + "), so this evidence was not captured for this plan's arm; if it was captured "
                                   + "for another evaluation, import it as origin reused with reusedFrom");
                }
            }
        }
        if (captured is { } at)
        {
            var created = PyDateTime.FromIso(Ev.Str(plan.Get("createdUtc")).Replace("Z", "+00:00"));
            if (created is not { Aware: true } made)
                errors.Add($"origin-unverified: the plan's createdUtc {PyText.Repr(plan.Get("createdUtc"))} is not a "
                           + "timezone-aware timestamp, so a fresh capture time cannot be compared with it");
            else if (at.Microseconds < made.Microseconds)
                errors.Add($"origin-mismatch: capturedUtc {Ev.Str(doc.Get("capturedUtc"))} is before this plan was "
                           + $"created ({Ev.Str(plan.Get("createdUtc"))}), so it was not captured for this plan");
        }
        if (errors.Count > 0) return (errors, new PyDict());
        return ([], new PyDict
        {
            ["origin"] = "fresh",
            ["originCheck"] = new PyDict
            {
                ["token"] = token, ["initializeRequests"] = count,
                ["capturedUtc"] = doc.Get("capturedUtc"), ["planCreatedUtc"] = plan.Get("createdUtc"),
            },
        });
    }
}
