using System.Numerics;
using HousecarlCore.Progress;
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

    const double ImportT0 = 1767225600.0;   // the generator's time of writing for every capture file

    public static void RunImport(Action<string, Func<string?>> @case)
    {
        Case = @case;
        PyDict ka;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("import.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            ka = D(PyJson.Loads(r.ReadToEnd()));
        ImportTables(ka);
        foreach (var folder in L(ka["imports"]).Select(D)) ImportFolder(folder);
        foreach (var stage in L(ka["recordsStage"]).Select(D)) RecordsStageCase(stage);
        RecordsPythonCases();
    }

    // The capture's stand-in: the canned summary with the capture folder written into its paths.
    static byte[] Filled(object? doc, string cap) => PyJson.Utf8(PyJson.Dumps(doc).Replace("{cap}", PyOrder.Fwd(cap)));

    // One recorded run of the records stage, its capture and its imports stood in for.
    static void RecordsStageCase(PyDict c)
    {
        Case($"records stage: {S(c["name"])}", () =>
        {
            using var t = new Temp();
            string run = t.At("run"), sdir = t.At("run/records");
            t.Write("run/evaluation/ledger.json", PyJson.Utf8(PyJson.Dumps(new PyDict { ["entries"] = c["entries"] })));
            Directory.CreateDirectory(sdir);
            for (int i = 0; i < (long)c["earlier"]!; i++) Directory.CreateDirectory(PyPath.Join(sdir, $"capture-{i + 1:00}"));
            // the capture's Python is a file of this test's own, so the case does not need one on this machine
            var machine = new PyDict(D(c["machine"])) { ["python"] = t.Write("python.exe", []) };
            var ctx = new RunContext(D(Py.Copy(c["entry"])), machine, run);
            var prefix = machine.Get("heavyPrefix") as List<object?>;
            var started = new List<object?>();
            var exits = L(c["importExits"]);
            int imports = 0;
            string? wrong = null, refusal = null;
            (PyDict? bound, List<string> outputs) got = (null, []);
            var saved = ChildLaunch.RunJob;
            var savedOut = Report.Out;
            var printed = new StringWriter();
            string? before = Environment.GetEnvironmentVariable("HOUSECARL_RECORD_READER_CMD");
            Environment.SetEnvironmentVariable("HOUSECARL_RECORD_READER_CMD", (string?)c["envReader"]);
            Report.Out = printed;
            ChildLaunch.RunJob = (op, dir, deadline, job) =>
            {
                var argv = job.Argv.ToList();
                bool capture = job.Id == "records-capture";
                var more = new PyDict();
                if (op != "evalpipeline-" + job.Id) wrong ??= $"{job.Id} was started under the run-log name {op}";
                if (capture)
                {
                    const long backstop = 460;   // 150 minutes for each of three arms, and ten
                    double minutes = Math.Round(((deadline ?? 0) - ChildRunner.Now()) / 60);
                    if (prefix != null)
                    {
                        int cut = argv.IndexOf("--");
                        var slot = argv.Take(cut).ToList();
                        argv = argv.Skip(cut + 1).ToList();
                        string lockFile = PyOrder.Fwd(PyPath.Join(run, "HEAVY-LOCK-m-records-capture"));
                        string wantSlot = $"python S:/tools/idle_lock.py hold --lock {lockFile} --min-free-gb 16 --purpose evalpipeline m records --max-min {backstop}";
                        if (string.Join(" ", slot) != wantSlot) wrong ??= $"the slot command was {string.Join(" ", slot)}";
                        if (minutes != 240 + backstop + ChildLaunch.HoldMarginMin) wrong ??= $"under the slot the capture's deadline was {minutes} minutes";
                    }
                    else if (minutes != backstop) wrong ??= $"the capture's deadline was {minutes} minutes";
                    more = new PyDict { ["deadline_min"] = backstop, ["heavy"] = true, ["hold_min"] = backstop, ["purpose"] = "evalpipeline m records" };
                    if (argv[0] != S(machine["python"])) wrong ??= $"the capture was started with {argv[0]}";
                    if (argv[1] != PyOrder.Fwd(PyPath.Join(Checkout.Harness, "evalrecords.py"))) wrong ??= $"the capture's script was {argv[1]}";
                }
                else
                {
                    if (deadline != null) wrong ??= $"{job.Id} was started with a deadline";
                    if (argv[0] != Environment.ProcessPath) wrong ??= $"{job.Id} was started with {argv[0]}";
                }
                started.Add(new PyDict
                {
                    ["op"] = job.Id, ["dir"] = t.Mask(dir), ["script"] = capture ? PyPath.Name(argv[1]) : "evalrun.py",
                    ["argv"] = t.Mask((capture ? argv.Skip(2) : argv.Skip(1)).Cast<object?>().ToList()), ["more"] = more,
                });
                if (capture)
                {
                    string cap = argv[argv.IndexOf("--out") + 1];
                    string rel = Path.GetRelativePath(t.Root, PyPath.Str(cap));
                    if (c["summary"] != null) t.Write(PyPath.Join(rel, "capture-summary.json"), Filled(c["summary"], cap));
                    if (c["workers"] != null) t.Write(PyPath.Join(rel, "capture-workers.json"), PyJson.Utf8(PyJson.Dumps(c["workers"])));
                    if (c["captureLog"] != null) t.Write(Path.GetRelativePath(t.Root, PyPath.Join(dir, "records-capture.txt")), PyJson.Utf8(S(c["captureLog"])));
                    return new ChildResult(job.Id, c["captureExit"] == null ? null : (int)(long)c["captureExit"]!, 0.1, null, 1);
                }
                if (c["importLogs"] is true) t.Write(Path.GetRelativePath(t.Root, PyPath.Join(dir, job.Id + ".txt")), PyJson.Utf8("$ import\n(exit 0, 0.1s)\n\n"));
                object? code = exits[imports++];
                return new ChildResult(job.Id, code == null ? null : (int)(long)code, 0.1, null, 1);
            };
            try { got = StageRecords.Records(ctx, new PyDict(), sdir); }
            catch (Refusal r) { refusal = t.Mask(r.Message); }
            finally
            {
                ChildLaunch.RunJob = saved;
                Report.Out = savedOut;
                Environment.SetEnvironmentVariable("HOUSECARL_RECORD_READER_CMD", before);
            }
            var folders = PyOrder.SortedByCodePoint(Directory.GetDirectories(sdir, "*", SearchOption.AllDirectories)
                .Select(p => PyOrder.Fwd(Path.GetRelativePath(run, p)))).Cast<object?>().ToList();
            return wrong ?? Same("refusal", refusal, (string?)c["refusal"])
                   ?? SameJson("children", started, c["started"])
                   ?? Same("printed", t.Mask(printed.ToString().Replace("\r\n", "\n")), S(c["printed"]))
                   ?? Same("records.json", t.Mask(Lines(PyPath.Join(sdir, "records.json"))), (string?)c["record"])
                   ?? SameJson("folders", folders, PyOrder.SortedByCodePoint(Strings(c["folders"])).Cast<object?>().ToList())
                   ?? (refusal != null ? null
                       : SameJson("bound", got.bound, c["bound"]) ?? SameJson("outputs", t.Mask(got.outputs.Cast<object?>().ToList()), c["outputs"]));
        });
    }

    // Where the capture's Python is found: this controller's own rule (decision D7), so its own cases.
    static void RecordsPythonCases()
    {
        Case("records stage: the capture's Python is the machine file's, else the first python.exe on PATH, else one sentence", () =>
        {
            using var t = new Temp();
            string named = t.Write("named/python.exe", []), onPath = t.Write("second/python.exe", []);
            Directory.CreateDirectory(t.At("first"));
            var saved = StageRecords.SearchPath;
            RunContext Ctx(PyDict machine) => new(new PyDict(), machine, t.Root, "lab");
            string? Refused(PyDict machine)
            {
                try { return "found " + StageRecords.Python(Ctx(machine)); }
                catch (Refusal r) { return r.Message; }
            }
            try
            {
                StageRecords.SearchPath = () => $"{t.At("first")};\"{t.At("second")}\"; ;{t.At("none")}";
                string? why = Same("the machine file's python", StageRecords.Python(Ctx(new PyDict { ["python"] = named })), named)
                              ?? Same("python on PATH", StageRecords.Python(Ctx(new PyDict())), onPath);
                if (why != null) return why;
                string gone = Refused(new PyDict { ["python"] = t.At("gone/python.exe") })!;
                StageRecords.SearchPath = () => t.At("first");
                string none = Refused(new PyDict())!;
                StageRecords.SearchPath = () => null;
                string unset = Refused(new PyDict())!;
                foreach (string sentence in new[] { gone, none, unset })
                    if (!sentence.StartsWith("the lab records capture needs Python", StringComparison.Ordinal) || !sentence.Contains("key python", StringComparison.Ordinal))
                        return "the refusal does not say that the capture needs Python and name the machine key: " + sentence;
                return gone.Contains(t.At("gone/python.exe"), StringComparison.Ordinal) ? null : "the refusal does not name the file the machine file gave: " + gone;
            }
            finally { StageRecords.SearchPath = saved; }
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
                        if (step["edits"] != null) ApplyState(t, step["edits"]);
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
                        if (step["live"] is true)
                            // a live driver's export is not read here: one sentence that says so and names the Python command
                            wantErr = "REFUSED: wrong-check: r.live is a live game driver's check, and runcontrol does not read a live driver's export "
                                      + "(evallive.py's validator is not part of it); import it with the Python controller: evalrun.py import-results.\n";
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
