using HousecarlCore.Progress;

namespace RunControl;

/// <summary>The command line of `runcontrol bundle`, as evalrun.py's `bundle` takes it.</summary>
public sealed class BundleArgs
{
    public string Run = "";
    public string? Out, Registry, Scope, Search, ReaderCmd;
    public bool NoNames;
    /// <summary>The copies and the manifest only: no names step and no order step (for another program using this
    /// library to bundle a run of its own; runcontrol's own command never sets it).</summary>
    public bool CopyOnly;
    public List<string> ReaderEnv = [], ModName = [];
}

/// <summary>`runcontrol bundle --run RUN [--out DIR] …` (evalrun.py's cmd_bundle): one evidence bundle assembled from
/// a finalized run. Copies the control files, receipts, decisions, candidates, every receipt's outputs and log, the
/// sweep's text files and the frozen premises; writes manifest.json listing what was copied and what a reader may
/// demand; then the names step and the order step. Exit 0, 2 for a refused command line or input, 3 for a run that
/// cannot be packaged.</summary>
public static class Bundle
{
    /// <summary>Suffixes never copied into a bundle: a plugin or asset is referred to by name and hash.</summary>
    public static readonly HashSet<string> BinarySuffixes = new(StringComparer.Ordinal)
    {
        ".esp", ".esm", ".esl", ".bsa", ".ba2", ".nif", ".dds", ".hkx", ".wav", ".xwm", ".fuz", ".exe", ".dll", ".pex", ".tri", ".seq", ".bik", ".lod",
    };

    public static readonly string[] ControlFiles = ["plan.json", "plan.sha256", "ledger.json", "journal.jsonl", "disposition.json", "STATUS.md"];

    static readonly string[] SweepSuffixes = [".json", ".jsonl", ".md", ".txt"];

    static int Refuse(string message)
    {
        Console.Error.WriteLine($"REFUSED: {message}");
        return Exit.Refused;
    }

    static bool IsBinary(string path) => BinarySuffixes.Contains(PyText.Lower(PyPath.Suffix(path)));

    /// <summary>Why a run cannot be packaged, naming the receipt and the path, or null.</summary>
    public static string? Refusal(string run, PyDict plan, Ledger ledger, PyDict receipts, List<object?> decisions)
    {
        // a control file missing from the run is a refusal here, never a path that drops out of the required list
        foreach (string name in ControlFiles)
        {
            if (name == "disposition.json") continue;   // its own cause below, with its own next action
            if (!Ev.Exists(PyPath.Join(run, name)))
                return $"control-missing: {name} is not in the run directory. Every control file is part of the "
                    + "bundle's required set, and a manifest that omitted one could not prove completeness. "
                    + "Next action: STATUS.md is rewritten by `finalize`; plan.json, plan.sha256, ledger.json "
                    + "and journal.jsonl cannot be regenerated, so without them this run is not packageable.";
        }
        if (PyJson.LoadEvalFile(PyPath.Join(run, "disposition.json")) is not PyDict disp)
            return $"disposition-missing: {PyPath.Join(run, "disposition.json")} is absent or unreadable. A bundle "
                + "carries a disposition's claims; there are none to carry. Next action: finalize first.";
        if (!Ev.Eq(disp.Get("planId"), plan["planId"]))
            return $"disposition-foreign: disposition.json carries planId {Ev.Str(disp.Get("planId"))}, this run is "
                + $"{Ev.Str(plan["planId"])}. Next action: finalize this run.";
        if (PyValue.Truthy(Ev.OrEmpty(disp.Get("evaluation")).Get("refused")))
            return "disposition-refused: the disposition records "
                + $"`{Ev.Str(PyValue.Dict(disp["evaluation"])["refused"])}` and makes no claim. Next action: fix the cause it "
                + "names, then finalize again.";
        var want = Disposition.DerivedFrom(run, receipts);
        object? got = disp.Get("derivedFrom");
        if (!Ev.Eq(got, want))
        {
            var g = Ev.OrEmpty(got);
            return "disposition-stale: the disposition was derived from ledger "
                + $"{Ev.Head(g.Get("ledgerSha256"), 12)} / receipts "
                + $"{Ev.Head(g.Get("receipts"), 12)} / journal {Ev.Str(g.Get("journalLines"))} "
                + $"line(s), the run now holds {Ev.Head(want["ledgerSha256"], 12)} / "
                + $"{Ev.Head(want["receipts"], 12)} / journal {Ev.Str(want["journalLines"])} line(s). Next action: "
                + "finalize again.";
        }
        foreach (var (k, e) in ledger.Entries)
        {
            if (!EvalStore.IsTerminalOk(Ev.G(e, "status"))) continue;
            if (!Ev.Exists(PyPath.Join(run, "receipts", $"{k}.json")))
                return $"receipt-missing: the ledger records {k} as {Ev.Str(PyValue.Dict(e)["status"])} but receipts/{k}.json is "
                    + "gone. Next action: resume --run RUN.";
        }
        // the same walk finalize used, so the two verbs cannot disagree about what a claim rests on
        var (integrity, _) = EvidenceIntegrity.Check(run, plan, ledger, receipts);
        foreach (var d in PyValue.Iter(integrity["defects"]).Select(PyValue.Dict))
        {
            string kind = Ev.Str(d["kind"]), key = Ev.Str(d["key"]), next = Ev.Str(d["nextAction"]);
            return kind switch
            {
                "evidence-missing" => $"evidence-missing: the ledger entry {key} claims {Ev.Str(d["path"])}, which is not on "
                    + "disk. A bundle never drops a required file from its manifest to stay complete. "
                    + $"Next action: {next}.",
                "evidence-changed" => $"evidence-changed: {key} recorded {Ev.Str(d["path"])} at {Ev.Head(d["recorded"], 12)}; it "
                    + $"now hashes {Ev.Head(d["found"], 12)}. A freshly computed hash is never adopted for a "
                    + $"receipt's output. Next action: {next}.",
                "evidence-dependency" => $"evidence-missing: {key} rests on {Ev.Str(d["recorded"])}, whose evidence is not what "
                    + $"was measured. Next action: {next}.",
                "tool-identity-missing" or "tool-identity-mismatch" =>
                    $"{kind}: {key} names {Ev.Str(d.Get("role"))} {ToolIdentity.Short(d["found"])}, the plan recorded "
                    + $"{ToolIdentity.Short(d["recorded"])} for {Ev.Str(d.Get("program"))}. Next action: {next}.",
                _ => $"receipt-mismatch: {key} — recorded {Ev.Str(d["recorded"])}, found {Ev.Str(d["found"])}. Next action: {next}.",
            };
        }
        foreach (var d in decisions.Select(PyValue.Dict))
        {
            if (!PyValue.Truthy(d.Get("accepted"))) continue;
            foreach (var row in Ev.Items(d.Get("evidence")).Select(PyValue.Dict))
                if (!Ev.Exists(PyPath.Join(run, Ev.Str(row["path"]))))
                    return $"decision-evidence-missing: the accepted decision on {Ev.Str(d["checkId"])} "
                        + $"[{Ev.Str(d["armId"])}] cites {Ev.Str(row["path"])}, which is not on disk. Next action: restore "
                        + "it, or withdraw the decision.";
        }
        return null;
    }

    /// <summary>The file bundle copies for each frozen premise: a file named on the command line, else the plan's
    /// stored path. Refused by name before anything is written (exit 2 for a named file, 3 for the stored one).</summary>
    public static (Dictionary<string, string> sources, string? bad, int code) PremiseSources(PyDict plan, IReadOnlyDictionary<string, string?> named)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, dest) in EvalStore.PremiseCopies)
        {
            var frozen = Ev.OrEmpty(plan.Get(label));
            string? given = string.IsNullOrEmpty(named.GetValueOrDefault(label)) ? null : named[label];
            if (!PyValue.Truthy(frozen.Get("path")))
            {
                if (given != null) return ([], $"--{label} {given}: this plan froze no {label}, so there is nothing to name", Exit.Refused);
                continue;   // a plan made without --scope has no scope premise
            }
            string want = Ev.Head(frozen.Get("sha256"), 12);
            string nxt = $"Next action: pass --{label} naming a copy that hashes {want}, such as an earlier bundle's {dest}.";
            if (given != null)
            {
                if (EvalStore.PremiseFileRefusal(plan, label, nxt, named: given) is string whyNamed) return ([], whyNamed, Exit.Refused);
                sources[label] = PyPath.Str(given);
                continue;
            }
            string? why = EvalStore.RelativePathRefusal(plan, only: [label], remedy: $"pass --{label} naming a copy that hashes {want}.")
                ?? EvalStore.PremiseFileRefusal(plan, label, nxt);
            if (why != null) return ([], why, Exit.Judgement);
            sources[label] = PyPath.Str(Ev.Str(frozen["path"]));
        }
        return (sources, null, Exit.Ok);
    }

    /// <summary>evalrun.store_file: src put at dst as a hard link when asked and possible, else as a copy; a file
    /// already at dst is removed first.</summary>
    public static string StoreFile(string src, string dst, bool link)
    {
        File.Delete(dst);
        if (link)
        {
            try
            {
                NativeFile.HardLink(dst, src);
                return "link";
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        File.Copy(src, dst);
        return "copy";
    }

    public static int Run(BundleArgs a)
    {
        string run = PyPath.Str(a.Run);
        EvalStore.SetRunStore(run);
        var (plan, why) = EvalStore.LoadPlan(run);
        if (plan == null) return Refuse(why!);
        string outDir = string.IsNullOrEmpty(a.Out) ? PyPath.Join(run, "bundle") : PyPath.Str(a.Out);
        if (a.ModName.FirstOrDefault(x => !x.Contains('=')) is string badName)
            return Refuse($"--mod-name takes PLUGIN=NAME, got {PyText.Repr(badName)}");
        var ledger = new Ledger(run, plan);
        if (ledger.Refusal() is string ledgerBad) return Refuse(ledgerBad);

        var receipts = Receipts.ReadReceipts(run);
        var decisions = Decisions.Load(run, plan);
        if (Refusal(run, plan, ledger, receipts, decisions) is string bad)
        {
            Console.Error.WriteLine($"REFUSED: {bad}");
            return Exit.Judgement;
        }
        var (premises, premiseBad, code) = PremiseSources(plan, new Dictionary<string, string?> { ["registry"] = a.Registry, ["scope"] = a.Scope, ["search"] = a.Search });
        if (premiseBad != null)
        {
            Console.Error.WriteLine($"REFUSED: {premiseBad}");
            return code;
        }

        var evidence = new List<object?>();
        PyDict st;
        using (var record = RunLog.Begin("evalrun-bundle", new RunOptions { StageSource = StageSource.Counted }))
        {
            try
            {
                record.Stage("bundle", total: 1, unit: "bundles");
                Assemble(run, outDir, plan, ledger, receipts, decisions, premises, evidence);
                record.Advance();
                if (a.CopyOnly)
                {
                    Console.Out.WriteLine($"bundle: {evidence.Count} evidence file(s) under {outDir}");
                    return Exit.Ok;
                }
                // the names step: each row's objects, their bases and models; a skip, a refusal or a partial read is stated
                record.Stage("names", total: 1, unit: "enrichments");
                if (a.NoNames)
                    st = BundleSteps.WriteNamesStatus(outDir, new PyDict
                    {
                        ["status"] = "skipped", ["reader"] = null,
                        ["sentence"] = "The names enrichment was skipped by request (--no-names), so "
                            + "the report cannot tell which objects have a body until "
                            + "`evalrun enrich --kind names` adds it.",
                    });
                else if (VerifyBundle.Run(outDir, quiet: true) != Exit.Ok)
                    st = BundleSteps.WriteNamesStatus(outDir, new PyDict
                    {
                        ["status"] = "refused", ["reader"] = null,
                        ["sentence"] = "The names enrichment was not run because the bundle does not verify (see its verify.json).",
                    });
                else
                {
                    var (cmd, source) = BundleSteps.DefaultReaderCmd(a.ReaderCmd);
                    st = BundleSteps.NamesStep(outDir, cmd, source, BundleSteps.ModNames(a.ModName), BundleSteps.ReaderEnv(a.ReaderEnv));
                    BundleSteps.WriteNamesStatus(outDir, st);
                    if (st["status"] is "enriched" or "partial" && VerifyBundle.Run(outDir, quiet: true) != Exit.Ok)
                    {
                        const string msg = "the names enrichment was written but the bundle no longer verifies; see its verify.json";
                        Console.Error.WriteLine($"REFUSED: {msg}");
                        record.Refuse(msg, Exit.Judgement);
                        return Exit.Judgement;
                    }
                }
                record.Advance();
                // the order step: whether the evaluated pair's order is a choice, in one sentence
                record.Stage("order", total: 1, unit: "statements");
                OrderEnrichment.Step(outDir);
                record.Advance();
            }
            catch (Exception ex)
            {
                record.Fail($"{ex.GetType().Name}: {ex.Message}");   // as Python's Run records an exception
                throw;
            }
        }
        Console.Out.WriteLine($"bundle: {evidence.Count} evidence file(s) under {outDir}");
        (st["status"] is "refused" or "skipped" or "partial" ? Console.Error : Console.Out)
            .WriteLine($"names: {Ev.Str(st["status"])}: {Ev.Str(st["sentence"])}");
        return Exit.Ok;
    }

    // The copies and the manifest.
    static void Assemble(string run, string outDir, PyDict plan, Ledger ledger, PyDict receipts, List<object?> decisions,
        Dictionary<string, string> premises, List<object?> evidence)
    {
        Directory.CreateDirectory(outDir);
        var copied = new Dictionary<string, PyDict>(StringComparer.Ordinal);
        string imports = PyPath.Resolve(PyPath.Join(run, "imports"));

        // Copies one file in and indexes it. When a receipt recorded a hash for it, the copy happens only if the file
        // still hashes to it, and the row carries the recorded hash and the receipt that recorded it.
        void CopyIn(string src, string relRaw, object? checkId = null, object? armId = null, object? strength = null, string kind = "artifact",
            object? provenance = null, object? recordedSha = null, object? recordedBy = null)
        {
            string rel = PyPath.AsPosix(relRaw);
            if (copied.ContainsKey(rel) || !Ev.Exists(src) || Directory.Exists(src)) return;
            if (IsBinary(src)) return;
            string? got = Hashing.Sha256FileOrNull(src);
            if (recordedSha != null && !Ev.Eq(got, recordedSha)) return;   // refused before this; never copied under a new hash
            string dst = PyPath.Join(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            // an import folder is written once and never changed, so its files are linked rather than copied again
            StoreFile(src, dst, link: PyPath.RelativeTo(PyPath.Resolve(src), imports) != null);
            var row = new PyDict
            {
                ["id"] = Hashing.Sha256Text(rel)[..16], ["kind"] = kind, ["path"] = rel, ["sha256"] = recordedSha ?? got,
                ["bytes"] = new FileInfo(dst).Length, ["checkId"] = checkId, ["armId"] = armId, ["strength"] = strength,
                ["provenance"] = provenance, ["recordedBy"] = recordedBy,
            };
            evidence.Add(row);
            copied[rel] = row;
        }

        var required = new List<object?>();
        var requiredSet = new HashSet<string>(StringComparer.Ordinal);
        // A required path is required whether or not the copy happened.
        void Require(string relRaw)
        {
            string rel = PyPath.AsPosix(relRaw);
            if (requiredSet.Add(rel)) required.Add(rel);
        }

        foreach (string name in ControlFiles) CopyIn(PyPath.Join(run, name), name, kind: "control");
        foreach (string sub in new[] { "receipts", "decisions", "candidates" })
        {
            string folder = PyPath.Join(run, sub);
            foreach (string p in PyOrder.SortedPaths(PyPath.RGlob(folder)))
                if (File.Exists(p)) CopyIn(p, $"{sub}/{PyPath.RelativeTo(p, folder)}", kind: sub.TrimEnd('s'));
        }
        foreach (var (_, recRaw) in receipts)
        {
            var rec = PyValue.Dict(recRaw);
            bool accepted = PyValue.Truthy(Ev.OrEmpty(rec.Get("validation")).Get("accepted"));
            foreach (var o in Ev.Items(rec.Get("outputs")).Select(PyValue.Dict))
            {
                string path = (string)o["path"]!;
                CopyIn(PyPath.Join(run, path), VerifyBundle.ReceiptEvidenceRel(path), checkId: rec["checkId"], armId: rec["armId"],
                    strength: Ev.OrEmpty(rec.Get("result")).Get("strength"), kind: "emit",
                    provenance: new PyDict { ["receiptId"] = rec["receiptId"], ["runId"] = rec.Get("runId") },
                    recordedSha: accepted ? o.Get("sha256") : null, recordedBy: accepted ? rec["receiptId"] : null);
            }
            if (PyValue.Truthy(rec.Get("log")))
            {
                // a sweep job log keeps its run-relative path: two launches write logs of the same name
                string log = (string)rec["log"]!, logRel = PyPath.AsPosix(log);
                CopyIn(PyPath.Join(run, log), logRel.StartsWith("sweep/", StringComparison.Ordinal) ? logRel : $"logs/{PyPath.Name(log)}",
                    checkId: rec["checkId"], armId: rec["armId"], kind: "log", provenance: new PyDict { ["receiptId"] = rec["receiptId"] });
            }
        }
        foreach (var d in decisions.Select(PyValue.Dict))
        {
            if (!PyValue.Truthy(d.Get("accepted"))) continue;
            foreach (var row in Ev.Items(d.Get("evidence")).Select(PyValue.Dict))
                CopyIn(PyPath.Join(run, (string)row["path"]!), $"evidence/{row["path"]}", checkId: d["checkId"], armId: d["armId"],
                    kind: "decision-evidence", strength: "attributed-decision", provenance: new PyDict { ["decidedBy"] = d.Get("decidedBy") });
        }
        string sweep = PyPath.Join(run, "sweep");
        foreach (string p in PyOrder.SortedPaths(PyPath.RGlob(sweep)))
            if (File.Exists(p) && SweepSuffixes.Contains(PyText.Lower(PyPath.Suffix(p))))
                CopyIn(p, $"sweep/{PyPath.RelativeTo(p, sweep)}", kind: "sweep");
        string Dest(string label) => EvalStore.PremiseCopies.First(c => c.label == label).dest;
        foreach (string label in new[] { "scope", "registry" })   // checked against the plan's frozen hashes above; manifest order kept
            if (premises.TryGetValue(label, out string? file)) CopyIn(file, Dest(label), kind: label);
        string? searchRel = null;
        var searchNote = new PyDict();
        if (premises.TryGetValue("search", out string? searchFile))
        {
            CopyIn(searchFile, Dest("search"), kind: "search");
            searchRel = Dest("search");
        }
        else if (plan.ContainsKey("search"))
        {
            object? absent = Ev.OrEmpty(plan.Get("search")).Get("absent");
            searchNote["searchAbsent"] = PyValue.Truthy(absent) ? absent : "the plan records no candidate search";
        }
        else
        {
            // a plan made before the search hash was frozen: copied as before, and said to be unfrozen
            string search = PyPath.Join((string)plan["candidatesDir"]!, "candidates.json");
            if (Ev.Exists(search))
            {
                CopyIn(search, Dest("search"), kind: "search");
                searchRel = Dest("search");
            }
            searchNote["searchUnfrozen"] = "the plan predates the frozen search hash, so search/candidates.json "
                + (searchRel != null ? "was copied unchecked" : "was not found to copy");
        }
        foreach (var (_, v) in searchNote) Console.Out.WriteLine($"   search: {Ev.Str(v)}");

        // what a reader may demand of this bundle: a manifest that only lists what it copied cannot prove completeness
        foreach (string name in ControlFiles) Require(name);
        Require("registry/check-registry.json");
        if (PyValue.Truthy(PyValue.Dict(plan["scope"]).Get("path"))) Require("scope/scope.json");
        foreach (var (k, e) in ledger.Entries)
            if (EvalStore.IsTerminalOk(Ev.G(e, "status"))) Require($"receipts/{k}.json");
        foreach (var (_, recRaw) in receipts)
        {
            var rec = PyValue.Dict(recRaw);
            if (!PyValue.Truthy(Ev.OrEmpty(rec.Get("validation")).Get("accepted"))) continue;
            foreach (var o in Ev.Items(rec.Get("outputs")).Select(PyValue.Dict))
            {
                string path = (string)o["path"]!;
                if (IsBinary(path)) continue;   // never copied by policy (identity only), so never a required path
                Require(VerifyBundle.ReceiptEvidenceRel(path));
            }
        }
        foreach (var d in decisions.Select(PyValue.Dict))
            if (PyValue.Truthy(d.Get("accepted")))
                foreach (var row in Ev.Items(d.Get("evidence")).Select(PyValue.Dict)) Require($"evidence/{row["path"]}");

        var manifest = new PyDict
        {
            ["bundleSchema"] = (long)EvalStore.BundleSchema, ["planId"] = plan["planId"], ["createdUtc"] = EvalStore.UtcNow(),
            ["runIds"] = Ev.G(ledger.Doc, "runIds", new List<object?>()),
            ["required"] = required,
            ["evidence"] = evidence,
            ["plan"] = "plan.json", ["ledger"] = "ledger.json", ["journal"] = "journal.jsonl",
            ["disposition"] = "disposition.json", ["status"] = "STATUS.md",
            ["registry"] = "registry/check-registry.json",
            ["search"] = searchRel,
        };
        manifest.Update(searchNote);
        manifest["scope"] = "scope/scope.json";
        manifest["excluded"] = new List<object?>
        {
            new PyDict
            {
                ["what"] = "plugin and asset binaries",
                ["why"] = "identities only: a shareable report package refers to a plugin by name "
                    + "and content hash and never carries its bytes",
            },
        };
        manifest["note"] = "bundle assembly and evaluation completeness are two different states: this bundle can "
            + "be complete while the evaluation is not. Read disposition.json for the second.";
        PyJson.WriteEvalFile(PyPath.Join(outDir, "manifest.json"), manifest);
    }
}
