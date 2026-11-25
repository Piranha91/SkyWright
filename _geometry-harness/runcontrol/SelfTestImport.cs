using System.Numerics;
using HousecarlCore.Progress;
using SkyWright.Engine;
using System.Reflection;
using System.Text;

namespace RunControl;

// The known answers for `import-results`: the timestamps it reads, the capture token and the initialize requests of a
// request file, a rows file, the empty population rule, the origin checks, and whole imports over synthetic run
// folders, each against evalrun.py's own answer (known-answers/make_known_answers_import.py). No Python runs here.
public static partial class SelfTestFinalize
{
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

    static Action<string, string> Skip = (_, _) => { };

    const double ImportT0 = 1767225600.0;   // the generator's time of writing for every capture file

    public static void RunImport(Action<string, Func<string?>> @case, Action<string, string> skip)
    {
        Case = @case;
        Skip = skip;
        PyDict ka;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("import.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            ka = D(PyJson.Loads(r.ReadToEnd()));
        ImportTables(ka);
        foreach (var folder in L(ka["imports"]).Select(D)) ImportFolder(folder);
        // the record capture is the AI-driven evaluation's (the module's own suite holds its recorded rows); this
        // program's records stage starts no child: the in-place row, and the open rows of a lab run
        foreach (var stage in L(ka["recordsStage"]).Select(D)) RecordsStageCase(stage);
        OpenRowsCases();
    }

    // One recorded run of the records stage that starts no child.
    static void RecordsStageCase(PyDict c)
    {
        Case($"records stage: {S(c["name"])}", () =>
        {
            using var t = new Temp();
            string run = t.At("run"), sdir = t.At("run/records");
            t.Write("run/evaluation/ledger.json", PyJson.Utf8(PyJson.Dumps(new PyDict { ["entries"] = c["entries"] })));
            Directory.CreateDirectory(sdir);
            for (int i = 0; i < (long)c["earlier"]!; i++) Directory.CreateDirectory(PyPath.Join(sdir, $"capture-{i + 1:00}"));
            var ctx = new RunContext(D(Py.Copy(c["entry"])), new PyDict(D(c["machine"])), run);
            var started = new List<object?>();
            string? refusal = null;
            (PyDict? bound, List<string> outputs) got = (null, []);
            var saved = ChildLaunch.RunJob;
            var savedOut = Report.Out;
            var printed = new StringWriter();
            Report.Out = printed;
            ChildLaunch.RunJob = (op, dir, deadline, job) =>
            {
                started.Add(new PyDict { ["op"] = job.Id });
                return new ChildResult(job.Id, 0, 0.1, null, 1);
            };
            try { got = StageRecords.Records(ctx, new PyDict(), sdir); }
            catch (Refusal r) { refusal = t.Mask(r.Message); }
            finally
            {
                ChildLaunch.RunJob = saved;
                Report.Out = savedOut;
            }
            var folders = PyOrder.SortedByCodePoint(Directory.GetDirectories(sdir, "*", SearchOption.AllDirectories)
                .Select(p => PyOrder.Fwd(Path.GetRelativePath(run, p)))).Cast<object?>().ToList();
            return Same("refusal", refusal, (string?)c["refusal"])
                   ?? SameJson("children", started, c["started"])
                   ?? Same("printed", t.Mask(printed.ToString().Replace("\r\n", "\n")), S(c["printed"]))
                   ?? Same("records.json", t.Mask(Lines(PyPath.Join(sdir, "records.json"))), (string?)c["record"])
                   ?? SameJson("folders", folders, PyOrder.SortedByCodePoint(Strings(c["folders"])).Cast<object?>().ToList())
                   ?? (refusal != null ? null
                       : SameJson("bound", got.bound, c["bound"]) ?? SameJson("outputs", t.Mask(got.outputs.Cast<object?>().ToList()), c["outputs"]));
        });
    }

    // A lab run's records stage after the evaluation's own capture (or without one): it starts no child, leaves the
    // capture's records.json alone, and lists every mcp-only row still open.
    static void OpenRowsCases()
    {
        (PyDict? bound, List<string> outputs, string printed, List<string> jobs, Temp t) Stage(PyDict entries, string? captureRecord)
        {
            var t = new Temp();
            t.Write("run/evaluation/ledger.json", PyJson.Utf8(PyJson.Dumps(new PyDict { ["entries"] = entries })));
            Directory.CreateDirectory(t.At("run/records"));
            if (captureRecord != null) t.Write("run/records/records.json", PyJson.Utf8(captureRecord));
            var entry = D(PyJson.Loads("{\"schema\": 1, \"id\": \"m\", \"subject\": {\"plugin\": \"S.esp\", \"mod\": \"MS\"}, "
                + "\"source\": {\"instance\": \"S:/src\", \"profile\": \"P\"}, \"lab\": {\"instance\": \"L:/lab\", \"game\": \"G:/game\"}}"));
            var ctx = new RunContext(entry, new PyDict { ["readerCmd"] = "reader" }, t.At("run"));
            var jobs = new List<string>();
            var saved = ChildLaunch.RunJob;
            var savedOut = Report.Out;
            var printed = new StringWriter();
            Report.Out = printed;
            ChildLaunch.RunJob = (op, dir, deadline, job) => { jobs.Add(job.Id); return new ChildResult(job.Id, 0, 0.1, null, 1); };
            try
            {
                var (bound, outputs) = StageRecords.Records(ctx, new PyDict(), t.At("run/records"));
                return (bound, outputs, printed.ToString(), jobs, t);
            }
            finally { ChildLaunch.RunJob = saved; Report.Out = savedOut; }
        }
        Case("records stage: a lab run's open mcp-only rows are listed by arm, and nothing is started", () =>
        {
            var entries = D(PyJson.Loads("{\"r.fields__baseline\": {\"checkId\": \"r.fields\", \"status\": \"mcp-only\"}, "
                + "\"r.fields__candidate-01\": {\"checkId\": \"r.fields\", \"status\": \"accepted\"}, "
                + "\"r.cells__candidate-01\": {\"checkId\": \"r.cells\", \"status\": \"mcp-only\"}, \"g.overlap__baseline\": {\"checkId\": \"g.overlap\", \"status\": \"accepted\"}}"));
            const string capture = "{\"capture\": {}, \"captureExit\": 0}";
            var (bound, outputs, printed, jobs, t) = Stage(entries, capture);
            using (t)
            {
                var doc = D(PyJson.ReadPipelineFile(t.At("run/records/" + StageRecords.OpenRowsFile)));
                return (jobs.Count == 0 ? null : $"the stage started {string.Join(", ", jobs)}")
                       ?? SameJson("open rows", doc["open"], PyJson.Loads("{\"baseline\": [\"r.fields\"], \"candidate-01\": [\"r.cells\"]}"))
                       ?? SameJson("bound", bound, PyJson.Loads("{\"openRows\": 2}"))
                       ?? SameJson("outputs", t.Mask(outputs.Cast<object?>().ToList()), PyJson.Loads("[\"<ROOT\\\\>\\\\run\\\\records\\\\open-rows.json\"]"))
                       ?? Same("the capture's record", File.ReadAllText(t.At("run/records/records.json")), capture)
                       ?? (printed.Contains("2 mcp-only row(s) on arm(s) baseline, candidate-01 have no imported record check") ? null : $"said: {printed}");
            }
        });
        Case("records stage: a lab run whose mcp-only rows were all imported lists none and says nothing", () =>
        {
            var entries = D(PyJson.Loads("{\"r.fields__baseline\": {\"checkId\": \"r.fields\", \"status\": \"accepted\"}}"));
            var (bound, _, printed, jobs, t) = Stage(entries, null);
            using (t)
            {
                var doc = D(PyJson.ReadPipelineFile(t.At("run/records/" + StageRecords.OpenRowsFile)));
                return (jobs.Count == 0 ? null : $"the stage started {string.Join(", ", jobs)}")
                       ?? SameJson("open rows", doc["open"], new PyDict())
                       ?? SameJson("bound", bound, PyJson.Loads("{\"openRows\": 0}"))
                       ?? (printed.Length == 0 ? null : $"said: {printed}")
                       ?? (File.Exists(t.At("run/records/records.json")) ? "the stage wrote records.json, which is the capture's" : null);
            }
        });
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
            return SameJson("errors", ImportResults.RowsOutputErrors(t.At(S(row["file"])), row["arm"] as PyDict).Cast<object?>().ToList(), row["want"]);
        }));
        Case("import: empty_population, when nothing judged is nothing to judge", () => Rows(L(ka["emptyPopulation"]), row =>
        {
            var members = L(row["members"]).Select(m => m == null ? (BigInteger?)null : PyValue.Int(m)).ToList();
            var (sentence, why) = ImportResults.EmptyPopulation(D(row["plan"]), D(row["arm"]), members, PyValue.Int(row["unjudgeable"]));
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
                        var (exit, stdout, stderr) = Captured(() => ImportResults.Import(t.Root, t.At(S(step["doc"])), $"import-run-{run}"));
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
}
