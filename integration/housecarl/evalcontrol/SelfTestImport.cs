using System.Numerics;
using RunControl;

namespace EvalControl;

// The known answers for a record import (moved from runcontrol's self-test with the record checks, under the names they
// had there, known-answers/import.json less the records stage's): the timestamps it reads, the capture token and the
// initialize requests of a request file, a rows file, the empty population rule, the origin checks, and whole imports
// over synthetic run folders, each against evalrun.py's own answer. The run folders are plain runs, not overlays, so the
// app scan's gate (RecordImport.AppGate) asks nothing of them; it has its own cases below. No Python runs here.
public static partial class SelfTest
{
    const double ImportT0 = 1767225600.0;   // the generator's time of writing for every capture file

    static void ImportCases(PyDict ka)
    {
        ImportTables(ka);
        foreach (var folder in L(ka["imports"]).Select(D)) ImportFolder(folder);
        AppGateCases();
    }

    // An overlay row's appPrerequisites, read from the app bundle the overlay is bound to (new with the move: in the app
    // registry the gate read the run's own ledger, and the import-results cases above still hold that rule). Each
    // status of the app's masters-and-order entry on an arm, and the answer the gate gives for an import on that arm.
    static void AppGateCases()
    {
        const string pre = "loadability.masters-and-order";
        string? Gate(string? baseline, string? candidate, string arm, bool bound = true, bool appPrerequisites = true)
        {
            using var t = new Temp("ai");
            var entries = new PyDict();
            if (baseline != null) entries[$"{pre}__baseline"] = new PyDict { ["checkId"] = pre, ["armId"] = "baseline", ["status"] = baseline };
            if (candidate != null) entries[$"{pre}__candidate-01"] = new PyDict { ["checkId"] = pre, ["armId"] = "candidate-01", ["status"] = candidate };
            var appPlan = new PyDict { ["planId"] = "app1", ["arms"] = new List<object?> { new PyDict { ["armId"] = "baseline" }, new PyDict { ["armId"] = "candidate-01" } } };
            t.Write("../app-bundle/plan.json", PyJson.Utf8(PyJson.Dumps(appPlan)));
            t.Write("../app-bundle/ledger.json", PyJson.Utf8(PyJson.Dumps(new PyDict { ["planId"] = "app1", ["entries"] = entries })));
            var plan = new PyDict { ["planId"] = "ai1" };
            if (bound) plan[OverlayPlan.Key] = new PyDict { ["appBundle"] = "../app-bundle" };
            var check = new PyDict { ["checkId"] = "records.cell-attributes", ["prerequisites"] = new List<object?>() };
            if (appPrerequisites) check["appPrerequisites"] = new List<object?> { pre };
            return t.Mask(RecordImport.AppGate(t.Root, plan, check, arm));
        }
        Case("import: an overlay row's app prerequisite, read from the bound app bundle's ledger", () =>
        {
            const string none = "blocked-prerequisite: the prerequisite loadability.masters-and-order has no accepted receipt on this arm";
            var rows = new (string what, string? got, string? want)[]
            {
                ("completed on the arm", Gate("completed", "completed", "candidate-01"), null),
                ("completed on the baseline only", Gate("completed", null, "candidate-01"), null),
                ("not applicable on the arm", Gate(null, "not-applicable", "candidate-01"), null),
                ("nothing settled, on the baseline", Gate(null, null, "baseline"), none + " in the app scan"),
                ("nothing settled", Gate(null, null, "candidate-01"), none + " (or on the baseline every arm is measured against) in the app scan"),
                ("rejected on the arm, completed on the baseline", Gate("completed", "rejected(identity-mismatch)", "candidate-01"),
                    "blocked-prerequisite: the prerequisite loadability.masters-and-order is rejected(identity-mismatch) on this arm, which decides for this arm: the baseline's answer does not settle it in the app scan"),
                ("a plan that is not an overlay", Gate(null, null, "baseline", bound: false), null),
                ("a row with no app prerequisite", Gate(null, null, "baseline", appPrerequisites: false), null),
            };
            foreach (var (what, got, want) in rows)
            {
                string? g = got == null ? null : got[..Math.Min(got.Length, want?.Length ?? got.Length)];
                if (g != want) return $"{what}: got {PyText.Repr(got)}, want the clause to start {PyText.Repr(want)}";
            }
            return null;
        });
    }

    // A receipt as text with what follows the folder's own path taken out: the import document's hash and size, and
    // each copied file's time of writing.
    static string? ReceiptText(Temp t, string path)
    {
        if (PyJson.LoadEvalFile(path) is not PyDict doc) return null;
        foreach (var row in Ev.Items(doc.Get("outputs")).Select(D))
        {
            row["mtimeUtc"] = "<mtime>";
            if (PyText.Str(row.Get("path")).EndsWith("/result.json", StringComparison.Ordinal))
            {
                row["sha256"] = "<document sha256>";
                row["bytes"] = "<document bytes>";
            }
        }
        if (doc.Get("imported") is PyDict imported) imported["sourceSha256"] = "<document sha256>";
        return t.Mask(PyJson.Dumps(doc, indent: 1, ensureAscii: false));
    }

    // A recorded plan (masked JSON text, deflated when long) as a plan under this root.
    static PyDict PlanOf(Temp t, object? packed) => D(PyJson.Loads(t.Unmask(Unpack(packed)!)));

    // A file's text with one line feed per line end, as Python's read_text gives it.
    static string? Lines(string path) => File.Exists(path) ? System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n") : null;

    // The recorded folder rebuilt, each file with the time of writing the generator gave it.
    static void BuildTimed(Temp t, object? tree)
    {
        BuildTree(t, tree);
        foreach (var row in L(tree).Select(D).Where(r => r.ContainsKey("mtime")))
            File.SetLastWriteTimeUtc(t.At(S(row["path"])), DateTime.UnixEpoch.AddSeconds(Py.Float(row["mtime"])));
    }

    // evalrun's utcnow replaced by the generator's counter for one step.
    static T Clocked<T>(Func<T> step)
    {
        var real = EvalStore.UtcNow;
        int n = 0;
        EvalStore.UtcNow = () => { n++; return $"2026-01-01T00:{n / 60:00}:{n % 60:00}Z"; };
        try { return step(); }
        finally { EvalStore.UtcNow = real; }
    }

    static byte[] Bytes(object? packed) => packed is PyDict z ? Inflate(S(z["z"])) : Convert.FromBase64String(S(packed));

    static void ImportTables(PyDict ka)
    {
        Case("import: datetime.fromisoformat as an import reads a timestamp", () => Rows(L(ka["fromiso"]), row =>
        {
            var got = PyDateTime.FromIso(S(row["text"]).Replace("Z", "+00:00"));
            string want = row["want"] is PyDict w ? $"{S(w["us"])} {(bool)w["aware"]!}" : "ValueError";
            return Same(PyText.Repr(S(row["text"])), got is { } g ? $"{g.Microseconds} {g.Aware}" : "ValueError", want);
        }));
        var requests = D(ka["requests"]);
        Case("import: capture_token", () => Rows(L(requests["tokens"]), row =>
            Same("token", ImportOrigin.CaptureToken(S(row["plan"]), S(row["arm"])), S(row["want"]))));
        Case("import: initialize_requests and client_name over request files", () => Rows(L(requests["files"]), row =>
        {
            var (found, why) = ImportOrigin.InitializeRequests(Bytes(row["bytes"]));
            return Same("why", why, (string?)row["why"])
                   ?? SameJson("names", found?.Select(ImportOrigin.ClientName).ToList(), row["names"]);
        }));
        Case("import: rows_output_errors, a rows file against the arm it is imported for", () => Rows(L(ka["rows"]), row =>
        {
            using var t = new Temp();
            if (row["text"] != null) t.Write(S(row["file"]), PyJson.Utf8(S(row["text"])));
            return SameJson("errors", RecordImport.RowsOutputErrors(t.At(S(row["file"])), row["arm"] as PyDict).Cast<object?>().ToList(), row["want"]);
        }));
        Case("import: empty_population, when nothing judged is nothing to judge", () => Rows(L(ka["emptyPopulation"]), row =>
        {
            var members = L(row["members"]).Select(m => m == null ? (BigInteger?)null : PyValue.Int(m)).ToList();
            var (sentence, why) = RecordImport.EmptyPopulation(D(row["plan"]), D(row["arm"]), members, PyValue.Int(row["unjudgeable"]));
            return Same("sentence", sentence, (string?)row["sentence"]) ?? Same("why", why, S(row["why"]));
        }));
        Case("import: origin_refusals on its own", () => Rows(L(ka["origin"]), row =>
        {
            using var t = new Temp();
            (string, string)? request = null;
            if (row["request"] is PyDict req) request = (t.Write("req.jsonl", Bytes(req["bytes"])), S(req["sha256"]));
            var captured = row["captured"] is string c ? PyDateTime.FromIso(c) : null;
            var (errors, fields) = ImportOrigin.Refusals(D(row["doc"]), PlanOf(t, row["plan"]), row["arm"], captured, request);
            return SameJson("errors", t.Mask(errors.Cast<object?>().ToList()), row["errors"]) ?? SameJson("fields", fields, row["fields"]);
        }));
    }

    // Every file under imports/: its path, its hash and whether it has a second name.
    static List<object?> ImportsListing(Temp t)
    {
        string root = t.At("imports");
        if (!Directory.Exists(root)) return [];
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => (rel: PyOrder.Fwd(Path.GetRelativePath(t.Root, p)), p))
            .OrderBy(x => x.rel, PyOrder.CodePoint)
            .Select(x => (object?)new PyDict { ["path"] = x.rel, ["sha256"] = PyPath.Name(x.p) == "result.json" ? null : Hashing.Sha256File(x.p), ["linked"] = NativeFile.LinkCount(x.p) > 1 }).ToList();
    }

    // One recorded run folder and the imports tried on it, one after the other.
    static void ImportFolder(PyDict c)
    {
        Case($"import-results: {S(c["name"])}", () =>
        {
            using var t = new Temp();
            BuildTimed(t, c["tree"]);
            var before = ImportResults.PreflightWith;
            ImportResults.PreflightWith = (_, _) => c["preflight"] is string refusal
                ? (refusal, new PyDict())
                : (null, new PyDict { ["controller"] = new PyDict { ["kind"] = "scripts", ["digest"] = "scripts:" + new string('c', 64) } });
            try
            {
                return Clocked(() =>
                {
                    int n = 0;
                    foreach (var step in L(c["steps"]).Select(D))
                    {
                        n++;
                        string name = S(step["name"]);
                        foreach (var f in L(step["files"]).Select(D))
                            File.SetLastWriteTimeUtc(t.Write(S(f["path"]), RowBytes(f, t)), DateTime.UnixEpoch.AddSeconds(ImportT0));
                        if (step["edits"] != null)
                            foreach (var f in L(step["edits"]).Select(D)) t.Write(S(f["path"]), RowBytes(f, t));
                        string journal = t.At("journal.jsonl");
                        int lines = File.Exists(journal) ? PyText.SplitLines(Lines(journal)!).Count : 0;
                        int run = n;
                        var (exit, stdout, stderr) = Captured(() => RecordImport.Import(t.Root, t.At(S(step["doc"])), $"import-run-{run}"));
                        string wantErr = S(step["stderr"]), gotErr = t.Mask(stderr)!;
                        if (step["runtimeWords"] is true)
                        {
                            // the words in brackets after "not readable as JSON" are each runtime's own
                            const string cut = "not readable as JSON (";
                            wantErr = wantErr[..(wantErr.IndexOf(cut, StringComparison.Ordinal) + cut.Length)];
                            int at = gotErr.IndexOf(cut, StringComparison.Ordinal);
                            gotErr = at < 0 ? gotErr : gotErr[..(at + cut.Length)];
                        }
                        string? key = (string?)step["key"];
                        string receipt = key == null ? "" : t.At($"receipts/{key}.json");
                        var ledger = File.Exists(t.At("ledger.json")) ? PyJson.LoadEvalFile(t.At("ledger.json")) as PyDict : null;
                        object? entry = key != null && ledger?.Get("entries") is PyDict entries ? t.Mask(entries.Get(key)) : null;
                        var wantImports = L(step["imports"]).Select(D).OrderBy(x => S(x["path"]), PyOrder.CodePoint).Cast<object?>().ToList();
                        string? bad = Same("exit", exit.ToString(), PyText.Str(step["exit"]))
                                      ?? Same("standard error", gotErr, wantErr)
                                      ?? Same("standard output", t.Mask(stdout), S(step["stdout"]))
                                      ?? Same("receipt", key != null && File.Exists(receipt) ? ReceiptText(t, receipt) : null, Unpack(step["receipt"]))
                                      ?? SameJson("ledger entry", entry, step["entry"])
                                      ?? SameJson("journal lines", File.Exists(journal) ? PyText.SplitLines(Lines(journal)!).Skip(lines).Cast<object?>().ToList() : new List<object?>(),
                                                  step["journal"])
                                      ?? SameJson("imports folder", ImportsListing(t), wantImports);
                        if (bad != null) return $"step '{name}': {bad}";
                    }
                    return null;
                });
            }
            finally { ImportResults.PreflightWith = before; }
        });
    }

    // ---- the record-rows enrichment's check (moved from runcontrol's verify-bundle with the kind; names as there):
    // runcontrol's verify-bundle walks and hashes the bundle, this module judges the rows it did not, and the recorded
    // verdict (exit, and verify.json's defects) must be reached again
    static void RecordsVerifyCases(PyDict ka)
    {
        foreach (var c in L(ka["cases"]).Select(D))
            Case($"verify-bundle: '{S(c["name"])}'", () =>
            {
                using var t = new Temp("b");
                BuildTree(t, ka["base"]);
                ApplyOps(t, c["ops"]);
                string target = PyPath.Join(t.Base, "verify-out.json");
                Clocked(() => Captured(() => VerifyBundle.Run(t.Root, target, quiet: true)));
                var verify = D(PyJson.LoadEvalFile(target));
                var block = D(verify["bundle"]);
                var found = new List<object?>();
                void Defect(string kind, object? path, string detail) => found.Add(new PyDict { ["kind"] = kind, ["path"] = path, ["detail"] = detail });
                var manifest = D(PyJson.LoadEvalFile(t.At("manifest.json")));
                var plan = D(PyJson.LoadEvalFile(t.At("plan.json")));
                var (kept, _) = RecordEvidence.Rejudge(t.Root, block, manifest, plan, Defect);
                bool complete = PyValue.Truthy(block.Get("schemaSupported")) && kept.Count == 0 && found.Count == 0
                    && PyValue.Len(Ev.OrList(block.Get("missing"))) == 0 && PyValue.Len(Ev.OrList(block.Get("mismatched"))) == 0;
                var recorded = D(D(PyJson.Loads(t.Unmask(Unpack(c["verify"])!)))["bundle"]);
                // runcontrol's other defects first, as it lists them, then the module's, in the order the rows were judged
                var got = kept.Concat(found).Select(x => (object?)t.Mask(x)).ToList();
                var want = Ev.Items(recorded["defects"]).Select(x => (object?)t.Mask(x)).ToList();
                return Same("exit", (complete ? Exit.Ok : Exit.Judgement).ToString(), ((long)c["exit"]!).ToString())
                    ?? SameJson("defects", Sorted(got), Sorted(want));
            });
        static List<object?> Sorted(List<object?> rows) => rows.OrderBy(PyJson.Canonical, StringComparer.Ordinal).ToList();
    }
}
