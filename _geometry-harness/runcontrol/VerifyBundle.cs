namespace RunControl;

/// <summary>`runcontrol verify-bundle DIR [--out FILE]` (evalrun.py's cmd_verify_bundle): structural validity, then
/// hashes, then the receipt-to-evidence chain, reading nothing outside the bundle. Writes verify.json (or FILE) and
/// exits 0 when the bundle is complete, else 3. The evaluation's own completeness is copied, never judged.</summary>
public static class VerifyBundle
{
    static readonly string[] ManifestKeys =
        ["bundleSchema", "planId", "required", "evidence", "plan", "ledger", "journal", "disposition", "status", "registry"];

    /// <summary>Where a bundle keeps a file a receipt names: bundle-relative, under evidence/.</summary>
    public static string ReceiptEvidenceRel(string receiptPath) => $"evidence/{receiptPath}";

    /// <summary>The registry version the AI-side checks left SkyWright's registry at: a bundle whose plan records an
    /// older one (or none) may hold rows of kinds this SkyWright no longer checks.</summary>
    public const int OldBundleBelow = 5;

    const string NotCheckedWhy = "made before the AI-side checks left SkyWright's registry: hashed and chained, not judged here";

    /// <summary>The one sentence verify-bundle prints for the rows it did not judge.</summary>
    public static string NotCheckedSentence(IReadOnlyCollection<object?> rows) =>
        $"verify: {rows.Count} evidence row(s) are of kinds this SkyWright does not check ("
        + string.Join(", ", rows.Select(r => Ev.Str(PyValue.Dict(r)["kind"])).Distinct(StringComparer.Ordinal))
        + "): they are hashed and chained, not judged; the houseCARL integration module checks them.";

    // (v or {}) of a loaded file.
    static PyDict Loaded(string path) => Ev.OrEmpty(PyJson.LoadEvalFile(path));

    /// <summary>Verifies the bundle and writes the result. quiet: nothing is printed (bundle's own in-process checks).</summary>
    public static int Run(string dir, string? outFile = null, bool quiet = false)
    {
        string d = PyPath.Str(dir);
        string At(object? rel) => PyPath.Join(d, Ev.Str(rel));
        var defects = new List<object?>();
        var missing = new List<object?>();
        var mismatched = new List<object?>();
        long ok = 0, identityCount = 0;
        var memo = new HashMemo();
        object? guarantee = null;
        var receiptOutputs = new List<object?>();
        var unjudged = new List<(object? path, string kind)>();
        var notChecked = new List<object?>();
        void Defect(string kind, object? path, string detail) =>
            defects.Add(new PyDict { ["kind"] = kind, ["path"] = path, ["detail"] = detail });

        // (1) the manifest itself
        bool schemaSupported = false;
        if (PyJson.LoadEvalFile(At("manifest.json")) is not PyDict manifest)
        {
            manifest = new PyDict();
            Defect("manifest-missing", "manifest.json", $"{At("manifest.json")} is absent or not readable as JSON: this is not a bundle.");
        }
        else
        {
            if (!Ev.Eq(manifest.Get("bundleSchema"), (long)EvalStore.BundleSchema))
                Defect("schema-unsupported", "manifest.json",
                    $"bundleSchema {PyText.Repr(manifest.Get("bundleSchema"))} is not {EvalStore.BundleSchema}: this bundle "
                    + "predates evidence-chain verification; rebuild it from its run.");
            else schemaSupported = true;
            var absent = ManifestKeys.Where(k => !manifest.ContainsKey(k)).ToList();
            if (absent.Count > 0) Defect("manifest-incomplete", "manifest.json", "the manifest does not carry " + string.Join(", ", absent));
        }

        if (schemaSupported)
        {
            // (2) required paths, then every listed evidence row against its recorded hash
            foreach (var rel in Ev.Items(manifest.Get("required")))
                if (!Ev.Exists(At(rel))) Defect("required-missing", rel, "a path the manifest declares required is not present");
            foreach (var e in Ev.Items(manifest.Get("evidence")).Select(PyValue.Dict))
            {
                string p = At(e["path"]);
                if (!Ev.Exists(p)) { missing.Add(e["path"]); continue; }
                string? got = memo.Sha256(p);
                if (!Ev.Eq(got, e["sha256"])) mismatched.Add(new PyDict { ["path"] = e["path"], ["recorded"] = e["sha256"], ["found"] = got });
                else ok++;
            }

            // (3) the plan's own immutability and the planId every document agrees on
            var plan = Loaded(At("plan.json"));
            guarantee = IdentityChecks.Guarantee(plan)["guarantee"];
            object? version = Ev.OrEmpty(plan.Get("registry")).Get("registryVersion");
            bool older = !PyValue.IsIntOrBool(version) || PyValue.Int(version) < OldBundleBelow;
            string recordedPlan = At("plan.sha256");
            if (!Ev.Exists(At("plan.json")) || !Ev.Exists(recordedPlan))
                Defect("plan-missing", "plan.json", "the bundle carries no plan and its recorded hash");
            else if (memo.Sha256(At("plan.json")) != PyValue.Strip(PyJson.Decode(File.ReadAllBytes(recordedPlan), bom: false)))
                Defect("plan-changed", "plan.json", "plan.json does not hash to plan.sha256");
            var ledgerDoc = Loaded(At("ledger.json"));
            var disp = Loaded(At("disposition.json"));
            var ids = new PyDict
            {
                ["manifest"] = manifest.Get("planId"), ["plan"] = plan.Get("planId"),
                ["ledger"] = ledgerDoc.Get("planId"), ["disposition"] = disp.Get("planId"),
            };
            if (ids.Select(kv => PyValue.Hash(kv.Value)).Distinct(StringComparer.Ordinal).Count() > 1)
                Defect("planid-disagreement", null,
                    "the manifest, plan, ledger and disposition do not agree on planId: " + PyJson.Canonical(ids));

            // (4) the registry, scope and candidate-search copies against the hashes the plan froze
            foreach (var (label, fallback) in EvalStore.PremiseCopies)
            {
                object? rel = PyValue.Truthy(manifest.Get(label)) ? manifest[label] : fallback;
                object? want = Ev.OrEmpty(plan.Get(label)).Get("sha256");
                if (want == null) continue;   // a plan made without --scope froze no scope hash; a search recorded absent froze none
                if (!Ev.Exists(At(rel)))
                    Defect($"{label}-missing", rel, $"the plan froze a {label} hash but the copy is absent");
                else if (!Ev.Eq(memo.Sha256(At(rel)), want))
                    Defect($"{label}-mismatch", rel,
                        $"the {label} copy hashes {Ev.Head(memo.Sha256(At(rel)), 12)}, the plan froze {Ev.Head(want, 12)}");
            }

            // (5) the receipt-to-evidence chain, walked from the ledger
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in Ev.Items(manifest.Get("evidence"))) listed.Add(PyValue.Hash(PyValue.Dict(e)["path"]));
            var entries = Ev.OrEmpty(ledgerDoc.Get("entries"));
            foreach (var (k, eRaw) in entries)
            {
                var e = PyValue.Dict(eRaw);
                if (!EvalStore.IsTerminalOk(e.Get("status"))) continue;
                if (PyJson.LoadEvalFile(At($"receipts/{k}.json")) is not PyDict rec)
                {
                    Defect("receipt-missing", $"receipts/{k}.json",
                        $"the ledger records {k} as {Ev.Str(e.Get("status"))} and the bundle carries no receipt");
                    continue;
                }
                string rid = Ev.Str(rec.Get("receiptId"));
                // a receipt's acceptance, identity and outputs are checked below whatever its kind; in an older bundle
                // one of an adapter kind this SkyWright's registry no longer has (a record import's) is named as well,
                // since the rules of its kind were applied by the controller that made it and are not repeated here
                if (older && (Ev.OrEmpty(rec.Get("adapter")).Get("kind") is not string rk || !Registry.App.AdapterKinds.Contains(rk)))
                    unjudged.Add(($"receipts/{k}.json", $"receipt of adapter kind {Ev.Str(Ev.OrEmpty(rec.Get("adapter")).Get("kind"))}"));
                if (PyValue.Truthy(e.Get("receiptId")) && !Ev.Eq(rec.Get("receiptId"), e.Get("receiptId")))
                    Defect("receipt-mismatch", $"receipts/{k}.json", $"the ledger names receipt {Ev.Str(e.Get("receiptId"))}, the file is {rid}");
                if (!PyValue.Truthy(Ev.OrEmpty(rec.Get("validation")).Get("accepted")))
                    Defect("receipt-mismatch", $"receipts/{k}.json", "a terminal-ok ledger entry whose receipt was not accepted");
                // the identity each role was accepted under, against the bundle's own plan: no tool is read
                foreach (var x in IdentityChecks.Defects(plan, rec))
                {
                    identityCount++;
                    Defect(x.Kind, $"receipts/{k}.json",
                        $"receipt {rid} names its {x.Role} {x.Program} at {ToolIdentity.Short(x.Found)}; the plan recorded {ToolIdentity.Short(x.Recorded)}");
                }
                // its outputs and the run-internal emits it was compared against
                var chain = new List<(object? path, object? recorded, bool isOutput)>();
                foreach (var o in Ev.Items(rec.Get("outputs"))) chain.Add((Ev.G(o, "path"), Ev.G(o, "sha256"), true));
                var consumed = Ev.OrEmpty(rec.Get("consumed"));
                if (PyValue.Truthy(consumed.Get("baseEmitPath"))) chain.Add((consumed["baseEmitPath"], consumed.Get("baseEmitSha256"), false));
                foreach (var row in Ev.Items(consumed.Get("inputs"))) chain.Add((Ev.G(row, "path"), Ev.G(row, "sha256"), false));
                foreach (var (path, recorded, isOutput) in chain)
                {
                    if (!PyValue.Truthy(path)) continue;
                    string rel = ReceiptEvidenceRel(Ev.Str(path));
                    if (isOutput)
                    {
                        bool present = Ev.Exists(At(rel));
                        receiptOutputs.Add(new PyDict
                        {
                            ["receipt"] = k, ["receiptId"] = rec.Get("receiptId"), ["checkId"] = rec.Get("checkId"),
                            ["armId"] = rec.Get("armId"), ["part"] = rec.Get("part"), ["recordedPath"] = path, ["path"] = rel,
                            ["sha256"] = recorded, ["present"] = present,
                            ["matches"] = present && recorded != null && Ev.Eq(memo.Sha256(At(rel)), recorded),
                        });
                    }
                    if (!Ev.Exists(At(rel)))
                    {
                        Defect("evidence-missing", rel, $"receipt {rid} records this file and the bundle omits it");
                        continue;
                    }
                    if (recorded != null && !Ev.Eq(memo.Sha256(At(rel)), recorded))
                        Defect("evidence-changed", rel,
                            $"receipt {rid} recorded {Ev.Head(recorded, 12)}, the copy hashes {Ev.Head(memo.Sha256(At(rel)), 12)}");
                    if (isOutput && !listed.Contains(PyValue.Hash(rel)))
                        Defect("manifest-omits-required", rel,
                            "a receipt's required output is present but not listed in the manifest, so the manifest cannot prove the bundle complete");
                }
            }
            foreach (var row in Ev.Items(disp.Get("manualDecisions")).Select(PyValue.Dict))
            {
                if (!PyValue.Truthy(row.Get("accepted"))) continue;
                foreach (var evRow in Ev.Items(row.Get("evidence")))
                {
                    string rel = $"evidence/{Ev.Str(PyValue.Dict(evRow)["path"])}";
                    if (!Ev.Exists(At(rel)))
                        Defect("decision-evidence-missing", rel,
                            $"the accepted decision on {Ev.Str(row.Get("checkId"))} cites evidence the bundle omits");
                }
            }

            // (6) the disposition must agree with the ledger it claims to be derived from
            var ev = Ev.OrEmpty(disp.Get("evaluation"));
            foreach (var (label, wantDetector) in new[] { ("findings", "finding"), ("completedNullChecks", "no-finding"), ("completedCatalogues", "catalogue") })
                foreach (var row in Ev.Items(ev.Get(label)))
                {
                    string key = Ledger.Key(Ev.G(row, "checkId"), Ev.G(row, "armId"), Ev.G(row, "part"));
                    object? entry = entries.Get(key);
                    if (!PyValue.Truthy(entry) || !EvalStore.IsTerminalOk(Ev.G(entry, "status")) || !Ev.Eq(Ev.G(entry, "detector"), wantDetector))
                        Defect("disposition-unsupported-claim", null,
                            $"disposition.evaluation.{label} names {key}, which the ledger does not hold as a terminal-ok {wantDetector} entry");
                }
            // (7) evidence derived after measurement, and the disposition it was derived beside
            EnrichmentVerify.Verify(d, manifest, plan, Defect, memo.Sha256,
                e => unjudged.Add((e.Get("path"), $"enrichment {Ev.Str(e.Get("enrichment"))}")));
            // (8) rows of kinds this SkyWright does not check: in a bundle whose plan records a registry older than the
            // one the AI-side checks left, they are hashed and chained above and listed, not judged; in any other bundle
            // this SkyWright made none can be there, so an enrichment of such a kind is a defect
            foreach (var (path, kind) in unjudged)
            {
                if (older) notChecked.Add(new PyDict { ["path"] = path, ["kind"] = kind, ["why"] = NotCheckedWhy });
                else Defect("evidence-kind-unknown", path,
                    $"an evidence row of kind {kind}, which this SkyWright neither writes nor checks, in a bundle whose plan records "
                    + $"registry version {Ev.Str(version)}: a bundle this SkyWright made holds none, so it is refused");
            }
            if (PyValue.Truthy(ev.Get("complete")) && (PyValue.Truthy(ev.Get("missing")) || PyValue.Truthy(ev.Get("unknownApplicability"))))
                Defect("disposition-complete-contradiction", "disposition.json",
                    "evaluation.complete is true while missing or unknownApplicability is non-empty");
            var wantDerived = Disposition.DerivedFrom(d, Receipts.ReadReceipts(d));
            if (!Ev.Eq(disp.Get("derivedFrom"), wantDerived))
                Defect("disposition-stale", "disposition.json",
                    $"the disposition was derived from ledger {Ev.Head(Ev.OrEmpty(disp.Get("derivedFrom")).Get("ledgerSha256"), 12)}, "
                    + $"the bundle carries {Ev.Head(wantDerived["ledgerSha256"], 12)}");
        }

        bool structurallyValid = schemaSupported && defects.Count == 0;
        bool complete = structurallyValid && missing.Count == 0 && mismatched.Count == 0;
        var evaluation = Ev.OrEmpty(Loaded(At("disposition.json")).Get("evaluation"));
        var verify = new PyDict
        {
            ["schemaVersion"] = (long)EvalStore.SchemaVersion, ["checkedUtc"] = EvalStore.UtcNow(), ["dir"] = PyPath.Resolve(d),
            ["bundleSchema"] = manifest.Get("bundleSchema"), ["planId"] = manifest.Get("planId"),
            ["bundle"] = new PyDict
            {
                ["schemaSupported"] = schemaSupported, ["structurallyValid"] = structurallyValid,
                ["complete"] = complete, ["verified"] = ok,
                ["missing"] = missing, ["mismatched"] = mismatched, ["defects"] = defects,
            },
            ["toolIdentity"] = new PyDict { ["guarantee"] = guarantee, ["defects"] = identityCount },
            // read from the bundle's disposition, never recomputed
            ["evaluation"] = new PyDict
            {
                ["complete"] = evaluation.Get("complete"), ["claim"] = evaluation.Get("claim"),
                ["missing"] = Ev.G(evaluation, "missing", new List<object?>()),
            },
            ["receiptOutputs"] = receiptOutputs,
            ["note"] = "every path is bundle-relative, so a relocated bundle validates identically; bundle "
                + "integrity and evaluation completion are two states and both are written here",
        };
        // only a bundle that has such rows says so: every other bundle's verify.json is as it was
        if (notChecked.Count > 0) PyValue.Dict(verify["bundle"])["notChecked"] = notChecked;
        PyJson.WriteEvalFile(PyValue.Truthy(outFile) ? outFile! : At("verify.json"), verify);
        if (!quiet)
        {
            Console.Out.WriteLine($"verify: {ok} file(s) verified, {missing.Count} missing, {mismatched.Count} mismatched, "
                + $"{defects.Count} defect(s); bundle.complete: {Ev.Str(complete)}; evaluation.complete: {Ev.Str(evaluation.Get("complete"))}");
            if (notChecked.Count > 0) Console.Out.WriteLine(NotCheckedSentence(notChecked));
            foreach (var x in defects.Take(20).Select(PyValue.Dict))
                Console.Error.WriteLine($"   {Ev.Str(x["kind"])}: {(PyValue.Truthy(x["path"]) ? Ev.Str(x["path"]) : "-")} — {Ev.Str(x["detail"])}");
        }
        return complete ? Exit.Ok : Exit.Judgement;
    }
}
