using System.Numerics;
using System.Text.RegularExpressions;

namespace RunControl;

/// <summary>The disposition finalize derives from the ledger and the receipts (evalrun.py's build_disposition and
/// what it calls), and the refusal disposition written when it cannot be derived.</summary>
public static class Disposition
{
    /// <summary>What an app scan says about the engine at play time: it observes none (it never launches the game).</summary>
    public const string NoRuntimeObservations = "no runtime observation was supplied to this run: nothing here says what the engine did at play time";

    /// <summary>What a disposition was derived from: the ledger's hash, one hash over the sorted (ledger key,
    /// receipt file hash) pairs, and the journal's count of non-blank lines.</summary>
    public static PyDict DerivedFrom(string root, PyDict? receipts = null)
    {
        receipts ??= Receipts.ReadReceipts(root);
        var pairs = PyOrder.SortedByCodePoint(receipts.Keys)
            .Select(k => (object?)new List<object?> { k, Hashing.Sha256FileOrNull(PyPath.Join(root, "receipts", $"{k}.json")) }).ToList();
        string journal = PyPath.Join(root, "journal.jsonl");
        long lines = 0;
        if (Ev.Exists(journal)) lines = PyText.SplitLines(Ev.ReadLoose(journal)).Count(ln => PyValue.Strip(ln).Length > 0);
        return new PyDict
        {
            ["ledgerSha256"] = Hashing.Sha256FileOrNull(PyPath.Join(root, "ledger.json")),
            ["receipts"] = Hashing.Sha256Text(PyJson.Canonical(pairs)), ["journalLines"] = lines,
        };
    }

    /// <summary>The refusal disposition and STATUS.md: complete false, claim incomplete, the cause named.</summary>
    public static void WriteRefusal(string run, string cause, string detail)
    {
        PyJson.WriteEvalFile(PyPath.Join(run, "disposition.json"), new PyDict
        {
            ["schemaVersion"] = (long)EvalStore.SchemaVersion, ["createdUtc"] = EvalStore.UtcNow(),
            ["derivedFrom"] = DerivedFrom(run),
            ["evaluation"] = new PyDict
            {
                ["complete"] = false, ["claim"] = "incomplete", ["refused"] = cause, ["detail"] = detail,
                ["missing"] = new List<object?>(), ["unknownApplicability"] = new List<object?>(),
                ["findings"] = new List<object?>(), ["completedNullChecks"] = new List<object?>(),
                ["completedCatalogues"] = new List<object?>(),
                ["integrity"] = new PyDict { ["ok"] = false, ["defects"] = new List<object?>() },
            },
        });
        Ev.WriteText(PyPath.Join(run, "STATUS.md"),
            "# Evaluation status — REFUSED\n\nThe finalizer refused to state a disposition.\n\n"
            + $"- cause: `{cause}`\n- detail: {detail}\n\n`evaluation.complete` is false and no claim is made.\n");
    }

    /// <summary>Each invocation that ran under a limit other than the plan's.</summary>
    public static List<object?> BudgetsInForce(Ledger ledger)
    {
        var rows = new List<object?>();
        foreach (var r in Ev.Items(ledger.Doc.Get("runIds")))
            if (Ev.G(r, "budgetMinutes") != null)
                rows.Add(new PyDict { ["runId"] = Ev.G(r, "runId"), ["command"] = Ev.G(r, "command"), ["budgetMinutes"] = PyValue.Dict(r)["budgetMinutes"] });
        return rows;
    }

    /// <summary>Validator.unjudgeable: the sum of a coverage line's unjudgeable counts.</summary>
    public static BigInteger Unjudgeable(PyDict line)
    {
        object? d = Ev.G(line, "Unjudgeable", line.Get("unjudgeable"));
        if (!PyValue.Truthy(d) || d is not PyDict counts) return 0;
        BigInteger sum = 0;
        foreach (var (_, v) in counts) sum += PyValue.Truthy(v) ? PyValue.Int(v) : 0;
        return sum;
    }

    // max(a, b) for two JSON numbers: the first unless the second is greater.
    static object? Max(object? a, object? b) => Greater(b, a) ? b : a;

    static bool Greater(object? x, object? y)
    {
        if (x is double || y is double) return AsDouble(x) > AsDouble(y);
        return PyValue.Int(x) > PyValue.Int(y);
    }

    static double AsDouble(object? v) => v is double d ? d : (double)PyValue.Int(v);


    static readonly Regex Bracket = new(@"\(.*\)$", RegexOptions.CultureInvariant);

    /// <summary>The ledger's entries counted by status, with a bracketed reason folded to (...), sorted by status.</summary>
    public static PyDict Counts(Ledger ledger)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (_, e) in ledger.Entries)
        {
            object? status = Ev.G(e, "status");
            string word = Bracket.Replace(PyValue.Truthy(status) ? (string)status! : "?", "(...)");
            counts[word] = counts.GetValueOrDefault(word) + 1;
        }
        return new PyDict(PyOrder.SortedByCodePoint(counts.Keys).Select(k => new KeyValuePair<string, object?>(k, counts[k])));
    }

    static object? Count(PyDict d, string key) => Ev.G(d, key, 0L);

    /// <summary>The disposition: what is missing, what was found, each arm's order evaluation, what was not assessed.</summary>
    public static PyDict Build(string run, PyDict plan, Ledger ledger, PyDict receipts, PyDict? integrity = null, PyDict? badKeys = null,
        IReadOnlyCollection<string>? neverCompleting = null)
    {
        integrity = PyValue.Truthy(integrity) ? integrity! : new PyDict { ["ok"] = true, ["defects"] = new List<object?>() };
        badKeys ??= new PyDict();
        var decisions = Decisions.Load(run, plan, neverCompleting);
        var decided = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in decisions.Select(PyValue.Dict))
            if (PyValue.Truthy(d.Get("satisfiesCompletion"))) decided.Add(PyValue.Hash(d["checkId"]) + "\0" + PyValue.Hash(d["armId"]));

        var missing = new List<object?>();
        var findingsRows = new List<object?>();
        var nullChecks = new List<object?>();
        var catalogues = new List<object?>();
        var unknownApp = new List<object?>();
        var entries = ledger.Entries;
        foreach (var check in PyValue.Iter(plan["checks"]).Select(PyValue.Dict))
        {
            object? cid = check["checkId"];
            bool counted = PyValue.Truthy(check["required"]) && check["applicability"] is "yes" or "unknown";
            if (Ev.Eq(check["applicability"], "unknown") && !Ev.Eq(check.Get("status"), EvalStore.NotOptedIn))
            {
                unknownApp.Add(new PyDict { ["checkId"] = cid, ["evidence"] = check["applicabilityEvidence"], ["effect"] = "keeps evaluation.complete false" });
                // listed under both unknownApplicability and missing: a check that ran on a target it may not apply to is not done
                foreach (var armRow in PyValue.Iter(check["arms"]))
                    missing.Add(new PyDict
                    {
                        ["checkId"] = cid, ["armId"] = PyValue.Dict(armRow)["armId"], ["part"] = null,
                        ["status"] = "unknown-applicability", ["required"] = check["required"], ["family"] = check["family"],
                        ["cause"] = "applicability is unknown, and unknown never becomes not-applicable: "
                            + (PyValue.Truthy(check["applicabilityEvidence"]) ? (string)check["applicabilityEvidence"]! : ""),
                    });
            }
            foreach (var armRow in PyValue.Iter(check["arms"]))
            {
                object? aid = PyValue.Dict(armRow)["armId"];
                var parts = entries.Keys.Where(k => Ev.Eq(PyValue.Dict(entries[k])["checkId"], cid) && Ev.Eq(PyValue.Dict(entries[k])["armId"], aid)).ToList();
                if (parts.Count == 0)
                {
                    if (counted)
                        missing.Add(new PyDict
                        {
                            ["checkId"] = cid, ["armId"] = aid, ["part"] = null, ["status"] = "pending",
                            ["cause"] = "the check was planned but no receipt was written",
                        });
                    continue;
                }
                foreach (string k in parts)
                {
                    var e = PyValue.Dict(entries[k]);
                    object? status = e.Get("status"), detector = e.Get("detector");
                    var r = Ev.OrEmpty(receipts.Get(k));
                    if (badKeys.ContainsKey(k))
                    {
                        // the evidence this entry's claim rests on is gone or changed: it completes nothing and is counted nowhere
                        missing.Add(new PyDict
                        {
                            ["checkId"] = cid, ["armId"] = aid, ["part"] = e.Get("part"), ["status"] = badKeys[k],
                            ["required"] = check["required"], ["family"] = check["family"],
                            ["cause"] = "the evidence this completed entry claimed no longer matches the receipt that recorded it; see evaluation.integrity",
                        });
                        continue;
                    }
                    bool terminal = EvalStore.IsTerminalOk(status);
                    if (!terminal) detector = null;
                    var result = Ev.OrEmpty(r.Get("result"));
                    if (Ev.Eq(detector, "finding"))
                        findingsRows.Add(new PyDict
                        {
                            ["checkId"] = cid, ["armId"] = aid, ["part"] = e.Get("part"),
                            ["findings"] = result.Get("findings"), ["verdict"] = result.Get("verdict"), ["strength"] = result.Get("strength"),
                        });
                    else if (Ev.Eq(detector, "no-finding"))
                        nullChecks.Add(new PyDict
                        {
                            ["checkId"] = cid, ["armId"] = aid, ["part"] = e.Get("part"),
                            ["coverage"] = (long)PyValue.Len(Ev.OrList(result.Get("coverage"))),
                        });
                    else if (Ev.Eq(detector, "catalogue"))
                    {
                        // rows that are evidence, not defects: never a finding, never a null check
                        object? covList = PyValue.Truthy(result.Get("coverage")) ? result["coverage"] : new List<object?> { new PyDict() };
                        var cov = PyValue.Dict(((List<object?>)covList!)[0]);
                        catalogues.Add(new PyDict
                        {
                            ["checkId"] = cid, ["armId"] = aid,
                            ["count"] = Ev.G(result, "findings", cov.Get("Judged")),
                            ["unit"] = Ev.G(cov, "Population", "rows"),
                            ["kind"] = Ev.OrEmpty(Ev.G(check["adapter"], "result")).Get("kind"),
                        });
                    }
                    if (!counted) continue;
                    bool ok = terminal && detector is "finding" or "no-finding" or "catalogue";
                    // an arm-level not-applicable is an answer, not an absence
                    if ((PyValue.Truthy(status) ? (string)status! : "").StartsWith("not-applicable", StringComparison.Ordinal)) ok = true;
                    if (!ok && decided.Contains(PyValue.Hash(cid) + "\0" + PyValue.Hash(aid))) ok = true;
                    if (!ok)
                    {
                        object? cause = PyValue.Truthy(e.Get("cause")) ? e["cause"]
                            : terminal && detector == null ? "detector-undetermined"
                            : "the check did not produce an accepted detector result";
                        missing.Add(new PyDict
                        {
                            ["checkId"] = cid, ["armId"] = aid, ["part"] = e.Get("part"), ["status"] = status, ["cause"] = cause,
                            ["required"] = true, ["family"] = check["family"],
                        });
                    }
                }
            }
        }

        bool complete = missing.Count == 0 && unknownApp.Count == 0;
        string claim = !complete ? "incomplete" : findingsRows.Count > 0 ? "findings" : "no-findings-in-completed-checks";

        var arms = new PyDict();
        foreach (var arm in PyValue.Iter(plan["arms"]).Select(PyValue.Dict))
        {
            // only accepted receipts: a rejected diff's classification is not this arm's order evaluation
            var rows = receipts.Select(kv => PyValue.Dict(kv.Value)).Where(r => Ev.Eq(r.Get("armId"), arm["armId"])
                && PyValue.Truthy(r.Get("orderEvaluation")) && PyValue.Truthy(Ev.OrEmpty(r.Get("validation")).Get("accepted"))).ToList();
            var perProbe = new PyDict();
            bool regression = false;
            var word = SweepReading.OrderClassification(run, arm, rows);
            var probes = new List<string>();
            foreach (var r in rows)
            {
                var oe = PyValue.Dict(r["orderEvaluation"]);
                foreach (var p in PyValue.Iter(Ev.G(oe, "probes", new List<object?>())))
                {
                    probes.Add((string)p!);
                    var created = Ev.OrEmpty(oe.Get("created"));
                    var counts = Ev.OrEmpty(Ev.OrEmpty(oe.Get("diff")).Get("counts"));
                    perProbe[(string)p!] = new PyDict
                    {
                        ["verdict"] = Ev.OrEmpty(r.Get("result")).Get("verdict"),
                        ["repaired"] = Count(counts, "repaired"), ["reclassified"] = Count(counts, "reclassified"),
                        ["created"] = Count(created, "created"), ["newDefect"] = Count(created, "newDefect"),
                        ["attributed"] = Count(created, "attributed"),
                        ["unmeasured"] = Max(Count(counts, "unmeasured"), Count(created, "unmeasured")),
                    };
                    if (PyValue.AboveZero(Count(created, "newDefect"))) regression = true;
                }
            }
            var orderEvaluation = new PyDict(word)
            {
                ["probes"] = PyOrder.SortedByCodePoint(probes.Distinct(StringComparer.Ordinal)).Cast<object?>().ToList(),
                ["regression"] = regression, ["perProbe"] = perProbe,
                ["scope"] = "ordersweep's own classification, verbatim, about the probes named and the population and state they ran in",
            };
            if (Ev.Eq(arm["kind"], "supplied-combination") && regression)
                orderEvaluation["note"] = "a supplied combination with a new physical defect on ANY probe is a regression whatever "
                    + "the repaired counts say: repairs on one probe never net against a defect on another";
            arms[Ev.Str(arm["armId"])] = new PyDict
            {
                ["kind"] = arm["kind"], ["candidateFile"] = arm["candidateFile"],
                ["loadedInGame"] = null, ["loadedInGameNote"] = arm["loadedInGameNote"],
                ["payload"] = arm.Get("payload"), ["orderEvaluation"] = orderEvaluation,
            };
        }

        var notExplored = new List<object?>(PyValue.Iter(PyValue.Dict(plan["states"])["notExplored"]));
        notExplored.AddRange(DeepStates.GeneratedNotExplored(run, plan));
        var unjudgeable = new List<object?>();
        foreach (var r in receipts.Select(kv => PyValue.Dict(kv.Value)))
            foreach (var c in Ev.Items(Ev.OrEmpty(r.Get("result")).Get("coverage")).Select(PyValue.Dict))
                if (Unjudgeable(c) > 0)
                    unjudgeable.Add(new PyDict
                    {
                        ["checkId"] = r["checkId"], ["armId"] = r["armId"],
                        ["population"] = Ev.G(c, "Population", c.Get("population")),
                        ["judged"] = Ev.G(c, "Judged", c.Get("judged")), ["unjudgeable"] = Ev.G(c, "Unjudgeable", c.Get("unjudgeable")),
                    });
        var families = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in PyValue.Iter(plan["checks"]).Select(PyValue.Dict))
            if (Ev.G(c["adapter"], "kind") is "unsupported" or "manual") families.Add((string)c["family"]!);
        var unassessed = new PyDict
        {
            ["statesNotExplored"] = notExplored,
            ["candidatesHeldBack"] = PyValue.Dict(plan["budgets"])["candidatesHeldBack"],
            ["unjudgeablePopulations"] = unjudgeable,
            ["familiesUnsupportedOrManual"] = PyOrder.SortedByCodePoint(families).Cast<object?>().ToList(),
            ["scopeUnmeasured"] = Ev.G(plan["scope"], "unmeasured", new List<object?>()),
            ["runtimeObservations"] = NoRuntimeObservations,
            ["notOptedIn"] = PyValue.Iter(plan["checks"]).Select(PyValue.Dict).Where(c => Ev.Eq(c.Get("status"), EvalStore.NotOptedIn))
                .Select(c => c["checkId"]).ToList(),
        };

        // (the discovery of existing patches and the author's advice are the houseCARL integration module's rows: its
        // overlay's disposition carries the discovery block; this scan's supplied patches are qualified in the
        // candidate-qualification receipt's derived block)

        var disposition = new PyDict
        {
            ["schemaVersion"] = (long)EvalStore.SchemaVersion, ["planId"] = plan["planId"], ["createdUtc"] = EvalStore.UtcNow(),
            ["registryVersion"] = PyValue.Dict(plan["registry"])["registryVersion"],
            ["runIds"] = Ev.G(ledger.Doc, "runIds", new List<object?>()),
            ["derivedFrom"] = DerivedFrom(run, receipts),
            ["evaluation"] = new PyDict
            {
                ["complete"] = complete, ["claim"] = claim, ["missing"] = missing, ["unknownApplicability"] = unknownApp,
                ["integrity"] = integrity, ["refused"] = PyValue.Truthy(integrity["ok"]) ? null : "evidence-integrity",
                ["toolIdentity"] = IdentityChecks.Guarantee(plan),
                ["findings"] = findingsRows, ["completedNullChecks"] = nullChecks, ["completedCatalogues"] = catalogues,
                ["claimVocabulary"] = EvalStore.Claims.Cast<object?>().ToList(),
                ["note"] = "the words pass, compatible and clean are not claims this finalizer makes; "
                    + "a completed check that found a defect is a completed check with a finding",
            },
            ["arms"] = arms,
        };
        if (PyValue.Truthy(plan.Get("reviewArm"))) disposition["reviewArm"] = plan["reviewArm"];
        disposition["manualDecisions"] = decisions;
        disposition["unassessed"] = unassessed;
        disposition["explicitDecisions"] = plan["explicitDecisions"];
        var budgets = BudgetsInForce(ledger);
        if (budgets.Count > 0) disposition["budgetsInForce"] = budgets;
        disposition["catalogues"] = catalogues;
        disposition["ledgerCounts"] = Counts(ledger);
        return disposition;
    }
}
