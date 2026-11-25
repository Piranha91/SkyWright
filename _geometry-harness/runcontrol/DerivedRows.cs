using HousecarlCore.Progress;
using SkyWright.Engine;

namespace RunControl;

/// <summary>The rows of the derived checks (adapter kind derived), as evalrun.py's run_derived computes them: from
/// other receipts, with no child and no measurement of their own. It computes the order evaluation of an arm from
/// its accepted diff parts, and the qualification of every supplied and discovered patch by its bytes.</summary>
public static class DerivedRows
{
    static bool Accepted(PyDict r) => PyValue.Truthy(PyValue.Dict(r["validation"])["accepted"]);

    static bool DiscoveryReceipt(PyDict r) => Ev.OrEmpty(r.Get("adapter")).Get("exe") is string exe && Discovery.DiscoveryExes.Contains(exe);

    // str(v or "").lower() when that is a sha256, else null.
    static string? Sha(object? v)
    {
        string s = PyText.Lower(PyValue.Truthy(v) ? Ev.Str(v) : "");
        return PlanArms.IsSha256(s) ? s : null;
    }

    static IEnumerable<PyDict> Vals(PyDict d) => d.Select(kv => PyValue.Dict(kv.Value));

    static string Short(string sha) => sha[..12] + "…";

    // A count read from a receipt, 0 when absent.
    static object Count(PyDict d, string key) => d.ContainsKey(key) ? d[key]! : 0L;

    /// <summary>The receipt ids of the accepted discovery receipts, in receipt order.</summary>
    public static List<object?> DiscoveryReceiptIds(PyDict receipts) =>
        Vals(receipts).Where(r => Accepted(r) && DiscoveryReceipt(r)).Select(r => r["receiptId"]).ToList();

    /// <summary>(rows, notRelevant, inconclusive sentences, not-searched sentences) from the accepted discovery
    /// receipts. A hit is relevant when its own masters, its FOMOD condition plugins or its pair name a plan target or
    /// a plan pair's plugin; it is qualified only when its sha256 equals a plugin an accepted receipt consumed. An
    /// output file is read only after its sha256 matches the one its receipt recorded.</summary>
    public static (List<object?> rows, PyDict notRelevant, List<object?> unreadable, List<object?> notSearched) DiscoveryHitRows(
        Executor ex, PyDict receipts, Dictionary<string, SortedSet<string>> consumedBy, Dictionary<string, HashSet<string>> byName)
    {
        var relevant = Discovery.RelevantPlugins(ex.Run, ex.Plan);
        var rows = new List<object?>();
        var seen = new Dictionary<(string file, string? sha, string source), PyDict>();
        var unreadable = new List<object?>();
        var notSearched = new List<object?>();
        var notRelevant = new PyDict();
        foreach (var r in PyOrder.SortedBy(Vals(receipts), x => Ev.Str(x.Get("receiptId"))))
        {
            if (!DiscoveryReceipt(r) || !Accepted(r)) continue;
            string exe = (string)PyValue.Dict(r["adapter"])["exe"]!;
            string rid = Ev.Str(r.Get("receiptId"));
            var outs = PyValue.Truthy(r.Get("outputs")) ? (List<object?>)r["outputs"]! : [];
            var o = outs.Count > 0 ? PyValue.Dict(outs[0]) : new PyDict();
            if (!PyValue.Truthy(o.Get("path")))
            {
                unreadable.Add($"receipt {rid} ({exe}) records no output file, so its hits cannot be read (a text-only receipt from before the --json hit list)");
                continue;
            }
            string path = Ev.Str(o["path"]);
            string f = PyPath.Join(ex.Run, path);
            if (!Ev.Exists(f))
            {
                unreadable.Add($"receipt {rid} ({exe}) names {path}, which is missing");
                continue;
            }
            if (!Ev.Eq(Hashing.Sha256FileOrNull(f), o.Get("sha256")))
            {
                unreadable.Add($"receipt {rid} ({exe}): {path} has changed since the receipt recorded its sha256");
                continue;
            }
            if (PyJson.LoadEvalFile(f) is not PyDict doc || !Ev.Eq(doc.Get("schemaVersion"), 1L) || doc.Get("hits") is not List<object?> hits)
            {
                unreadable.Add($"receipt {rid} ({exe}): {path} is not a schema-1 hit list");
                continue;
            }
            foreach (var h in hits.OfType<PyDict>())
            {
                if (!PyValue.Truthy(h.Get("file"))) continue;
                if (!Discovery.HitNames(h).Overlaps(relevant))
                {
                    if (!notRelevant.ContainsKey(exe)) notRelevant[exe] = new PyDict { ["count"] = 0L, ["why"] = Discovery.NotRelevantWhy };
                    var nr = (PyDict)notRelevant[exe]!;
                    nr["count"] = (long)nr["count"]! + 1;
                    continue;
                }
                string file = Ev.Str(h["file"]);
                string? hsha = Sha(h.Get("sha256"));
                string source = $"{exe}:{Ev.Str(h.Get("kind"))}";
                var key = (PyText.Lower(file), hsha, source);
                if (seen.TryGetValue(key, out var earlier))
                {
                    earlier["foundOn"] = PyOrder.SortedByCodePoint(Ev.Items(earlier["foundOn"]).Select(Ev.Str).Append(Ev.Str(r.Get("armId"))).Distinct(StringComparer.Ordinal))
                        .Cast<object?>().ToList();
                    continue;
                }
                bool hitOk = hsha != null && consumedBy.ContainsKey(hsha);
                string why;
                if (hsha == null)
                    why = $"the {exe} hit records no content hash ({(PyValue.Truthy(h.Get("unhashedReason")) ? Ev.Str(h["unhashedReason"]) : "no reason given")}), so "
                          + "its bytes cannot be compared with any evaluated arm's: a downloadable hit stays a lead until its payload is measured";
                else if (hitOk)
                    why = $"its sha256 {Short(hsha)} (from {exe}'s hit list) equals a plugin an accepted receipt consumed on {string.Join(", ", consumedBy[hsha])}";
                else
                {
                    var sameName = PyOrder.SortedByCodePoint((byName.GetValueOrDefault(PyText.Lower(PyPath.Name(file))) ?? []).Where(s => s != hsha));
                    why = $"its sha256 {Short(hsha)} (from {exe}'s hit list) matches no plugin any accepted receipt consumed"
                          + (sameName.Count > 0 ? $"; an accepted receipt consumed a different {file} (sha256 " + string.Join(", ", sameName.Select(Short)) + ")" : "")
                          + ": a downloadable hit stays a lead until its payload is measured";
                }
                var row = new PyDict
                {
                    ["file"] = file, ["armId"] = null, ["source"] = source, ["foundOn"] = new List<object?> { Ev.Str(r.Get("armId")) },
                    ["sha256"] = hsha, ["qualification"] = hitOk ? "qualified" : "unqualified", ["why"] = why,
                };
                seen[key] = row;
                rows.Add(row);
            }
        }
        var acceptedChecks = Vals(receipts).Where(r => Accepted(r) && DiscoveryReceipt(r)).Select(r => r.Get("checkId")).ToList();
        foreach (var c in Ev.Items(ex.Plan.Get("checks")).Select(PyValue.Dict))
        {
            object? exe = Ev.OrEmpty(c.Get("adapter")).Get("exe");
            if (exe is string e && Discovery.DiscoveryExes.Contains(e) && !PyValue.In(c.Get("checkId"), acceptedChecks))
                notSearched.Add($"{Ev.Str(c.Get("checkId"))} ({e}): no accepted receipt (plan status "
                                + $"{(PyValue.Truthy(c.Get("status")) ? Ev.Str(c["status"]) : "unknown")}), so the patches it would find are not stated");
        }
        return (rows, notRelevant, unreadable, notSearched);
    }

    /// <summary>Settles one planned row of a derived check that no earlier invocation completed: the derivation
    /// computed and receipted, or the row accounted.</summary>
    public static void Run(Executor ex, PyDict check, PyDict arm)
    {
        var adapter = PyValue.Dict(check["adapter"]);
        string cid = Ev.Str(check["checkId"]);
        object? aid = arm["armId"];
        var receipts = Receipts.ReadReceipts(ex.Run);
        var derivedFrom = new List<object?>();
        string? detector;
        object? findings;
        PyDict payload;

        // the prerequisite gate every other row passes: a derivation over an unsettled prerequisite would read an absence as an input
        bool Blocked()
        {
            if (Prerequisites.Satisfied(ex, check, aid) is not { } pre) return false;
            ex.Account(check, arm, $"blocked(prerequisite:{pre})", "blocked-prerequisite", Prerequisites.BlockDetail(ex, pre, aid));
            return true;
        }

        if (Ev.Eq(adapter.Get("derives"), "order-evaluation") || cid.EndsWith("order-evaluation", StringComparison.Ordinal))
        {
            if (Ev.Eq(arm["kind"], "baseline"))
            {
                // the baseline is the reference every other arm is measured against
                ex.Account(check, arm, "not-applicable", null,
                    "the baseline is the reference the other arms are measured against; an order evaluation of the baseline against itself is not a measurement");
                return;
            }
            if (Blocked()) return;
            // derived only from this arm's accepted diff parts: one the sweep planned for this arm that is not accepted blocks it
            var unsettled = new List<(string check, object? status)>();
            foreach (var c in PyValue.Iter(ex.Plan["checks"]).Select(PyValue.Dict))
            {
                if (!Ev.Eq(PyValue.Dict(c["adapter"]).Get("kind"), "ordersweep-probe") || !Ev.Eq(c.Get("status"), "planned")
                    || !Ev.Items(c.Get("arms")).Any(r => Ev.Eq(Ev.G(r, "armId"), aid) && Ev.Eq(Ev.G(r, "status"), "planned"))) continue;
                object? status = ex.Ledger.Entry(c["checkId"], aid, "diff")?.Get("status");
                if (!EvalStore.IsTerminalOk(status)) unsettled.Add((Ev.Str(c["checkId"]), status));
            }
            if (unsettled.Count > 0)
            {
                ex.Account(check, arm, $"blocked(prerequisite:{unsettled[0].check})", "blocked-prerequisite",
                    "an order evaluation derives only from this arm's accepted diff parts; not accepted: "
                    + string.Join(", ", unsettled.Select(u => $"{u.check} diff ({(PyValue.Truthy(u.status) ? Ev.Str(u.status) : "no entry")})")));
                return;
            }
            var rows = Vals(receipts)
                .Where(r => Ev.Eq(r.Get("armId"), aid) && Ev.Eq(r.Get("part"), "diff") && PyValue.Truthy(r.Get("orderEvaluation"))).ToList();
            derivedFrom = rows.Select(r => r["receiptId"]).ToList();
            var perProbe = new PyDict();
            var word = SweepReading.OrderClassification(ex.Run, arm, rows.Where(r => PyValue.Truthy(Ev.OrEmpty(r.Get("validation")).Get("accepted"))));
            foreach (var r in rows)
            {
                var oe = PyValue.Dict(r["orderEvaluation"]);
                var counts = Ev.OrEmpty(Ev.OrEmpty(oe.Get("diff")).Get("counts"));
                var created = Ev.OrEmpty(oe.Get("created"));
                object a = Count(counts, "unmeasured"), b = Count(created, "unmeasured");
                foreach (var p in PyValue.Iter(Ev.G(oe, "probes", new List<object?>())))
                    // the numbers, and only the numbers: whether they amount to a regression is decided in the finalizer
                    perProbe[Ev.Str(p)] = new PyDict
                    {
                        ["verdict"] = PyValue.Dict(r["result"]).Get("verdict"), ["counts"] = counts, ["created"] = created,
                        ["repaired"] = Count(counts, "repaired"), ["reclassified"] = Count(counts, "reclassified"),
                        ["createdRows"] = Count(created, "created"), ["newDefect"] = Count(created, "newDefect"),
                        ["attributed"] = Count(created, "attributed"), ["unmeasured"] = Py.Float(b) > Py.Float(a) ? b : a,
                    };
            }
            payload = new PyDict(word)
            {
                ["perProbe"] = perProbe,
                ["scope"] = "the probes named above, in the population and state they ran in",
                ["note"] = "this receipt carries the per-probe NUMBERS only; whether they amount to a regression is derived once, in the finalizer, from created.newDefect",
            };
            long newDefectProbes = Vals(perProbe).Count(v => Py.Float(v["newDefect"]) > 0);
            detector = rows.Count == 0 ? null
                : newDefectProbes > 0 ? "finding"
                : Vals(perProbe).Any(v => v["verdict"] is null or "DIFFERENTIAL-UNJUDGED") ? "inconclusive"
                : "no-finding";
            findings = newDefectProbes;
        }
        else if (Ev.Eq(adapter.Get("derives"), "candidate-qualification") || cid.EndsWith("candidate-qualification", StringComparison.Ordinal))
        {
            if (Blocked()) return;
            // qualification compares bytes: the sha256 each accepted receipt consumed. The name only labels the row.
            var consumedBy = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var byName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var r in Vals(receipts).Where(Accepted))
                foreach (var p in Ev.Items(Ev.OrEmpty(PyValue.Dict(Ev.G(r, "consumed", new PyDict())).Get("configuration")).Get("plugins")).Select(PyValue.Dict))
                {
                    if (Sha(p.Get("sha256")) is not { } sha) continue;
                    if (!consumedBy.ContainsKey(sha)) consumedBy[sha] = new SortedSet<string>(PyOrder.CodePoint);
                    consumedBy[sha].Add(Ev.Str(r.Get("armId")));
                    if (!PyValue.Truthy(p.Get("name"))) continue;
                    string name = PyText.Lower(PyPath.Name(Ev.Str(p["name"])));
                    if (!byName.ContainsKey(name)) byName[name] = new HashSet<string>(StringComparer.Ordinal);
                    byName[name].Add(sha);
                }
            derivedFrom = Vals(receipts).Where(Accepted).Select(r => r["receiptId"]).ToList();
            var manifest = PyJson.LoadEvalFile(PyPath.Join(ex.Run, "candidates", "candidates.json"));
            var rows = new List<object?>();
            foreach (var arm2 in PyValue.Iter(ex.Plan["arms"]).Select(PyValue.Dict))
            {
                // the supplied file's hash is the one this arm's frozen request recorded, never a fresh read
                var recorded = new Dictionary<string, PyDict>(StringComparer.Ordinal);
                foreach (var p in Ev.Items(Ev.OrEmpty(arm2.Get("requested")).Get("plugins")).Select(PyValue.Dict))
                    if (PyValue.Truthy(p.Get("name"))) recorded[PyText.Lower(PyPath.Name(Ev.Str(p["name"])))] = p;
                foreach (var declared in Ev.Items(PyValue.Dict(Ev.G(arm2, "supplied", new PyDict())).Get("declaredFiles")))
                {
                    string name = PyPath.Name((string)declared!);
                    string key = PyText.Lower(name);
                    var req = recorded.GetValueOrDefault(key);
                    string? sha = Sha(req?.Get("sha256"));
                    bool qualified = sha != null && consumedBy.ContainsKey(sha);
                    string why;
                    if (sha == null)
                        why = $"this arm's request records no content hash (sha256) for {name} ("
                              + (PyValue.Truthy(req?.Get("unstatedReason")) ? Ev.Str(req!["unstatedReason"]) : "the request does not list it")
                              + "), so its bytes cannot be compared with any evaluated arm's: a downloadable hit stays a lead until its payload is measured";
                    else if (qualified)
                        why = $"its sha256 {Short(sha)} (from this arm's request) equals a plugin an accepted receipt consumed on {string.Join(", ", consumedBy[sha])}";
                    else
                    {
                        var sameName = PyOrder.SortedByCodePoint((byName.GetValueOrDefault(key) ?? []).Where(s => s != sha));
                        why = $"its sha256 {Short(sha)} (from this arm's request) matches no plugin any accepted receipt consumed"
                              + (sameName.Count > 0 ? $"; an accepted receipt consumed a different {name} (sha256 " + string.Join(", ", sameName.Select(Short)) + ")" : "")
                              + ": a downloadable hit stays a lead until its payload is measured";
                    }
                    rows.Add(new PyDict
                    {
                        ["file"] = name, ["armId"] = arm2["armId"], ["sha256"] = sha,
                        ["qualification"] = qualified ? "qualified" : "unqualified", ["why"] = why,
                    });
                }
            }
            if (manifest is PyDict m)
                foreach (var c in Ev.Items(m.Get("Candidates")).Select(PyValue.Dict))
                {
                    string f = PyPath.Name(Ev.Str(Ev.G(c, "File", "")));
                    if (f.Length > 0)
                        rows.Add(new PyDict
                        {
                            ["file"] = f, ["armId"] = null, ["qualification"] = "catalogued",
                            ["why"] = "a candidate-order file, not a supplied patch payload",
                        });
                }
            // every discovered patch: each relevant hit of an accepted discovery receipt is one row, qualified by the same rule
            var (hitRows, notRelevant, unreadable, notSearched) = DiscoveryHitRows(ex, receipts, consumedBy, byName);
            rows.AddRange(hitRows);
            foreach (var rid in DiscoveryReceiptIds(receipts))
                if (!PyValue.In(rid, derivedFrom)) derivedFrom.Add(rid);
            payload = new PyDict { ["candidates"] = rows, ["notRelevant"] = notRelevant, ["notSearched"] = notSearched };
            findings = 0L;
            detector = "no-finding";
            if (unreadable.Count > 0)
            {
                // a hit list that cannot be read as written makes the answer inconclusive
                payload["inconclusive"] = unreadable;
                detector = "inconclusive";
                findings = null;
            }
        }
        else
        {
            ex.Account(check, arm, "unsupported", null,
                $"derived kind {PyText.Repr(adapter.Get("derives"))} is not one this controller computes (it computes order-evaluation and candidate-qualification)");
            return;
        }

        var v = new PyDict
        {
            ["accepted"] = true, ["rejections"] = new List<object?>(), ["detector"] = detector, ["findings"] = findings,
            ["coverage"] = new List<object?>(), ["verdict"] = null, ["identity"] = null,
        };
        string started = EvalStore.UtcNow(), ended = EvalStore.UtcNow();
        ex.Finish(check, arm, null, adapter, null, new ChildResult("derived", 0, 0.0, null, 1), started, ended, [], null, v,
            extra: new PyDict { ["derived"] = payload, ["derivedFrom"] = derivedFrom });
    }
}
