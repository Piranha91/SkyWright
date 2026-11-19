using System.Numerics;
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
