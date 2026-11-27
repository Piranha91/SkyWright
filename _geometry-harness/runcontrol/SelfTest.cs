using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace RunControl;

/// <summary>The known answers: every value here was computed by the Python controller before it was retired, and is
/// recorded in known-answers/*.json; this program must reach it exactly. Those are the known answers now: a behaviour
/// change gets a new one from what the game does. The cases in SelfTestOwnCases.cs were worked by hand.</summary>
public static partial class SelfTest
{
    static int _green, _red, _skipped;

    // One case: null from the body is a pass, a sentence is the reason it failed.
    static void Case(string name, Func<string?> body)
    {
        string? why;
        try { why = body(); }
        catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message}"; }
        if (why == null) { _green++; Console.WriteLine($"GREEN {name}"); }
        else { _red++; Console.WriteLine($"RED {name}: {why}"); }
    }

    static void Skip(string name, string why) { _skipped++; Console.WriteLine($"SKIP {name}: {why}"); }

    // ---- reading the answers

    static PyDict D(object? v) => (PyDict)v!;
    static List<object?> L(object? v) => (List<object?>)v!;
    static string S(object? v) => (string)v!;
    static List<string> Strings(object? v) => L(v).Select(S).ToList();
    static byte[] B64(object? v) => Convert.FromBase64String(S(v));
    static double Bits(object? v) => BitConverter.Int64BitsToDouble(unchecked((long)Convert.ToUInt64(S(v), 16)));

    // The first few rows of a table that do not hold, as one sentence; null when all hold.
    static string? Rows<T>(IEnumerable<T> rows, Func<T, string?> check)
    {
        var bad = new List<string>();
        int n = 0;
        foreach (var row in rows)
        {
            n++;
            string? why;
            try { why = check(row); }
            catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message}"; }
            if (why != null) bad.Add($"row {n}: {why}");
        }
        return bad.Count == 0 ? null : $"{bad.Count} of {n} rows differ; " + string.Join(" | ", bad.Take(3));
    }

    static string? Same(string what, string? got, string? want) =>
        got == want ? null : $"{what}: got {Show(got)}, Python has {Show(want)}";

    static string Show(string? s) => s == null ? "null" : PyText.Repr(s.Length > 160 ? s[..160] + "..." : s);

    static string? SameBytes(string what, byte[] got, byte[] want)
    {
        if (got.AsSpan().SequenceEqual(want)) return null;
        int at = 0;
        while (at < got.Length && at < want.Length && got[at] == want[at]) at++;
        int from = Math.Max(0, at - 20);
        string Around(byte[] b) => Show(Encoding.Latin1.GetString(b, Math.Min(from, b.Length), Math.Min(Math.Max(b.Length - from, 0), 60)));
        return $"{what}: the bytes differ at {at} (got {got.Length} bytes {Around(got)}, Python has {want.Length} bytes {Around(want)})";
    }

    // A value read from the answers, with {"__tuple__": [...]} turned back into a tuple.
    static object? FromWire(object? v) => v switch
    {
        PyDict d when d.Count == 1 && d.ContainsKey("__tuple__") => new PyTuple(L(d["__tuple__"]).Select(FromWire).ToList()),
        PyDict d => new PyDict(d.Select(kv => new KeyValuePair<string, object?>(kv.Key, FromWire(kv.Value)))),
        List<object?> l => l.Select(FromWire).ToList(),
        _ => v,
    };

    // ---- a temporary folder, named the way Python's resolve() would name it

    sealed class Temp : IDisposable
    {
        public string Root { get; }
        public Temp()
        {
            string made = Path.Combine(Path.GetTempPath(), "hc-runcontrol-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(made);
            Root = PyPath.Resolve(made);
        }
        public string At(string rel) => rel == "." ? Root : PyPath.Join(Root, rel);
        public string Mask(string s) => s.Replace(Root, "<ROOT\\>").Replace(PyOrder.Fwd(Root), "<ROOT/>");
        public object? Mask(object? v) => v switch
        {
            string s => Mask(s),
            PyDict d => new PyDict(d.Select(kv => new KeyValuePair<string, object?>(Mask(kv.Key), Mask(kv.Value)))),
            List<object?> l => l.Select(Mask).ToList(),
            _ => v,
        };
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static void SetMtimeNs(string path, long ns) =>
        File.SetLastWriteTimeUtc(path, new DateTime(DateTime.UnixEpoch.Ticks + ns / 100, DateTimeKind.Utc));

    // The answers' folder, rebuilt: the same bytes, hard links and last-write times Python hashed.
    static void BuildTree(PyDict tree, Temp t)
    {
        foreach (var f in L(tree["files"]).Select(D))
        {
            string p = t.At(S(f["path"]));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, B64(f["bytes"]));
        }
        foreach (var l in L(tree["links"]).Select(D))
        {
            string p = t.At(S(l["path"]));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            NativeFile.HardLink(p, t.At(S(l["to"])));
        }
        foreach (string d in Strings(tree["dirs"])) Directory.CreateDirectory(t.At(d));
        foreach (var f in L(tree["files"]).Select(D)) SetMtimeNs(t.At(S(f["path"])), (long)f["mtimeNs"]!);
    }

    // ---- the cases

    public static int Run()
    {
        PyDict ka;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("foundations.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            ka = D(PyJson.Loads(r.ReadToEnd()));
        Console.WriteLine($"known answers from Python {ka["python"]}");

        JsonCases(ka);
        TextCases(ka);
        TreeCases(ka);
        LedgerCases(ka);
        EntryCases(ka);
        PipelineCases();
        InPlaceCases();
        LabCases();
        PrepReuseCases();
        OwnerDifferenceCases();
        DriverCases();
        ChildCases();
        StageCases();
        MeshReachCases();
        BlackBoxCases();
        SelfTestPlanModules.Run(Case, Skip);
        PlanCases();
        IdentityCases();
        SelfTestSweep.Run(Case);
        SelfTestFinalize.Run(Case);
        SelfTestFinalize.RunEvaluate(Case);
        EvaluateStageCases();
        SelfTestFinalize.RunBundle(Case);
        SelfTestFinalize.RunRows(Case);
        EntryCopyCases();
        SelfTestFinalize.RunOwnCases(Case);
        BoundaryCases(Skip);
        LiveCases();
        SelfTestStranded.Run(Case, Skip);

        Console.WriteLine($"{_green} green, {_red} red, {_skipped} skipped");
        return _red == 0 ? 0 : 1;
    }

    static void JsonCases(PyDict ka)
    {
        using var t = new Temp();
        foreach (var row in L(ka["json"]).Select(D))
        {
            string name = S(row["name"]);
            object? doc = PyJson.Loads(S(row["src"]));
            Case($"json forms {name}", () => Rows(D(row["forms"]), kv =>
            {
                var opt = kv.Key.Split(' ').Select(x => x.Split('=')[1]).ToArray();
                string text = PyJson.Dumps(doc, indent: opt[0] is "c" or "n" ? null : int.Parse(opt[0]), sortKeys: opt[1] == "1",
                                           ensureAscii: opt[2] == "1", compact: opt[0] == "c");
                if (kv.Value == null)
                {
                    try { PyJson.Utf8(text); }
                    catch (EncoderFallbackException) { return null; }
                    return $"{kv.Key}: Python cannot encode this text and the port encoded it";
                }
                return SameBytes(kv.Key, PyJson.Utf8(text), B64(kv.Value));
            }));
            Case($"json sha256_json {name}", () => Same("hash", PyJson.Sha256Json(doc), S(row["sha256Json"])));
            Case($"json population form {name}", () =>
            {
                string text = PyJson.PopulationText(doc);
                PyJson.WritePopulationFile(t.At("population.json"), text);
                return SameBytes("text", PyJson.Utf8(text), B64(row["populationText"]))
                       ?? SameBytes("file", File.ReadAllBytes(t.At("population.json")), B64(row["populationFile"]));
            });
            if (!row.ContainsKey("canonical"))
            {
                Case($"json raw forms refuse {name}", () =>
                {
                    try { PyJson.WritePipelineFile(t.At("refused.json"), doc); }
                    catch (EncoderFallbackException) { return null; }
                    return "a lone surrogate was written to a file; Python refuses to encode it";
                });
                continue;
            }
            Case($"json canonical {name}", () =>
                SameBytes("text", PyJson.Utf8(PyJson.Canonical(doc)), B64(row["canonical"]))
                ?? Same("hash", Hashing.Sha256Text(PyJson.Canonical(doc)), S(row["canonicalSha256"])));
            Case($"json evalpipeline write_json {name}", () =>
            {
                PyJson.WritePipelineFile(t.At("out/pipeline.json"), doc);
                return SameBytes("file", File.ReadAllBytes(t.At("out/pipeline.json")), B64(row["pipelineFile"]))
                       ?? Same("read back", PyJson.Dumps(PyJson.ReadPipelineFile(t.At("out/pipeline.json"))), PyJson.Dumps(doc));
            });
            Case($"json evalrun write_json {name}", () =>
            {
                PyJson.WriteEvalFile(t.At("out/eval.json"), doc);
                return SameBytes("file", File.ReadAllBytes(t.At("out/eval.json")), B64(row["evalFile"]))
                       ?? Same("read back", PyJson.Dumps(PyJson.LoadEvalFile(t.At("out/eval.json"))), PyJson.Dumps(doc));
            });
        }
        Case("json parse", () => Rows(L(ka["parse"]).Select(D), row =>
        {
            byte[] raw = B64(row["bytes"]);
            string label = $"{Show(Encoding.Latin1.GetString(raw))} bom={row["bom"]}";
            object? value;
            try { value = PyJson.Loads(PyJson.Decode(raw, (bool)row["bom"]!)); }
            catch (FormatException) { return (bool)row["ok"]! ? $"{label}: Python reads this and the port refused it" : null; }
            if (!(bool)row["ok"]!) return $"{label}: Python refuses this and the port read it";
            return Same(label, PyJson.Dumps(value, compact: true), S(row["dump"]));
        }));
        Case("json load_json returns null for a missing or broken file", () =>
        {
            File.WriteAllText(t.At("broken.json"), "{\"a\":");
            return PyJson.LoadEvalFile(t.At("broken.json")) == null && PyJson.LoadEvalFile(t.At("nowhere.json")) == null
                ? null : "a value came back";
        });
    }

    static void TextCases(PyDict ka)
    {
        var text = D(ka["text"]);
        Case("float text (repr, json, .1f, .2f, .3f, g, round)", () => Rows(L(ka["floats"]).Select(D), row =>
        {
            double x = Bits(row["bits"]);
            string label = S(row["repr"]);
            return Same($"repr {label}", PyText.FloatRepr(x), S(row["repr"]))
                   ?? Same($"json {label}", PyText.FloatJson(x), S(row["json"]))
                   ?? Same($"g {label}", PyText.General(x), S(row["g"]))
                   ?? (row.ContainsKey("f1") ? Same($".1f {label}", PyText.Fixed(x, 1), S(row["f1"])) : null)
                   ?? (row.ContainsKey("f2") ? Same($".2f {label}", PyText.Fixed(x, 2), S(row["f2"])) : null)
                   ?? (row.ContainsKey("f3") ? Same($".3f {label}", PyText.Fixed(x, 3), S(row["f3"])) : null)
                   ?? (row.ContainsKey("round1") ? Same($"round {label}", PyText.FloatRepr(PyText.Round(x, 1)), S(row["round1"])) : null);
        }));
        Case("repr and str", () => Rows(L(text["repr"]).Select(D), row =>
        {
            object? v = FromWire(PyJson.Loads(S(row["src"])));
            return Same("repr", PyText.Repr(v), S(row["repr"])) ?? Same("str", PyText.Str(v), S(row["str"]));
        }));
        Case("splitlines", () => Rows(L(text["splitlines"]).Select(D), row =>
            Same(Show(S(row["src"])), PyText.Repr(PyText.SplitLines(S(row["src"])).Cast<object?>().ToList()), PyText.Repr(row["lines"]))));
        Case("split", () => Rows(L(text["split"]).Select(D), row =>
            Same(Show(S(row["src"])), PyText.Repr(PyText.Split(S(row["src"])).Cast<object?>().ToList()), PyText.Repr(row["parts"]))));
        Case("sorted and sorted(key=str.lower)", () => Rows(L(text["sorted"]).Select(D), row =>
            Same("by code point", string.Join("|", PyOrder.SortedByCodePoint(Strings(row["items"]))), string.Join("|", Strings(row["byCodePoint"])))
            ?? Same("by lower key", string.Join("|", PyOrder.SortedByLowerKey(Strings(row["items"]))), string.Join("|", Strings(row["byLower"])))));
        Case("sorted(paths)", () => Rows(L(text["sortedPaths"]).Select(D), row =>
            Same("order", string.Join("|", PyOrder.SortedPaths(Strings(row["items"]))), string.Join("|", Strings(row["sorted"])))));
        Case("path spellings (str, as_posix, name, suffix, normpath, normcase)", () => Rows(L(text["paths"]).Select(D), row =>
        {
            string p = S(row["p"]);
            return Same($"str {Show(p)}", PyPath.Str(p), S(row["str"])) ?? Same($"as_posix {Show(p)}", PyPath.AsPosix(p), S(row["posix"]))
                   ?? Same($"name {Show(p)}", PyPath.Name(p), S(row["name"])) ?? Same($"suffix {Show(p)}", PyPath.Suffix(p), S(row["suffix"]))
                   ?? Same($"normpath {Show(p)}", PyPath.NormPath(p), S(row["normpath"]))
                   ?? Same($"normcase {Show(p)}", PyPath.NormCase(p), S(row["normcase"]));
        }));
        Case("same_path", () => Rows(L(text["samePath"]).Select(D), row =>
        {
            string a = S(row["a"]), b = S(row["b"]);
            return Same($"evalpipeline {Show(a)} {Show(b)}", PyOrder.SamePath(a, b).ToString(), row["pipeline"]!.ToString())
                   ?? Same($"evalrun {Show(a)} {Show(b)}", PyOrder.SamePathGiven(a, b).ToString(), row["evalrun"]!.ToString());
        }));
        Case("relative_to", () => Rows(L(text["relativeTo"]).Select(D), row =>
            Same($"{row["child"]} under {row["parent"]}", PyPath.RelativeTo(S(row["child"]), S(row["parent"])), (string?)row["rel"])));
        Case("iso_of", () => Rows(L(text["isoOf"]).Select(D), row => Same(S(row["bits"]), PyText.IsoOf(Bits(row["bits"])), S(row["iso"]))));
        Case("utcnow spellings", () => Rows(L(text["utc"]).Select(D), row =>
        {
            var when = DateTime.UnixEpoch.AddSeconds((long)row["unix"]!);
            return Same("+00:00", PyText.IsoPlus(when), S(row["plus"])) ?? Same("Z", PyText.IsoZ(when), S(row["z"]));
        }) ?? (Regex.IsMatch(PyText.UtcNowPlus(), AtShape) ? null : "utcnow has another shape"));
        Case("sha256_text", () => Rows(L(text["sha256Text"]).Select(D), row => Same(Show(S(row["src"])), Hashing.Sha256Text(S(row["src"])), S(row["sha256"]))));
        Case("dotted", () =>
        {
            object? doc = PyJson.Loads("{\"a\": {\"b\": [10, {\"c\": null}]}, \"n\": null, \"l\": [[5]], \"z\": 0, \"s\": \"text\", \"\": {\"\": 1}}");
            return Rows(L(text["dotted"]).Select(D), row => Same(S(row["path"]), PyJson.Dumps(PyJson.Dotted(doc, S(row["path"]))), S(row["value"])));
        });
        Case("exit codes", () =>
        {
            var e = D(ka["exitCodes"]);
            return (long)e["ok"]! == Exit.Ok && (long)e["refused"]! == Exit.Refused && (long)e["judgement"]! == Exit.Judgement ? null : "they differ";
        });
    }

    static void TreeCases(PyDict ka)
    {
        var tree = D(ka["tree"]);
        using var t = new Temp();
        BuildTree(tree, t);
        Case("tree last-write times read back in nanoseconds", () => Rows(L(tree["files"]).Select(D), f =>
            Same(S(f["path"]), Hashing.MtimeNs(t.At(S(f["path"]))).ToString(), f["mtimeNs"]!.ToString())));
        Case("tree sorted(rglob)", () => Same("order",
            string.Join("|", PyOrder.SortedPaths(PyPath.RGlob(t.Root)).Select(p => PyPath.RelativeTo(p, t.Root))),
            string.Join("|", Strings(tree["rglobSorted"]))));
        Case("tree output fingerprints", () => Rows(D(tree["fingerprint"]), kv => Same(kv.Key, Hashing.Fingerprint(t.At(kv.Key)), (string?)kv.Value)));
        Case("tree tool digests", () => Rows(D(tree["toolDigest"]), kv => Same(kv.Key, Hashing.ToolDigest(t.At(kv.Key)), (string?)kv.Value)));
        Case("tree unique bytes count a hard-linked file once", () =>
            Same("whole", Hashing.UniqueBytes(t.Root).ToString(), tree["uniqueBytes"]!.ToString())
            ?? Same("without top files", Hashing.UniqueBytes(t.Root, topFiles: false).ToString(), tree["uniqueBytesNoTop"]!.ToString())
            ?? Same("links folder", Hashing.UniqueBytes(t.At("links")).ToString(), tree["uniqueBytesLinks"]!.ToString())
            ?? ((long)tree["plainBytes"]! > (long)tree["uniqueBytes"]! ? null : "the fixture has no hard link to count once"));
        Case("tree sha256 of files, and the memo", () => Rows(D(tree["sha256File"]), kv =>
        {
            var memo = new HashMemo();
            return Same(kv.Key, Hashing.Sha256File(t.At(kv.Key)), S(kv.Value)) ?? Same($"memo {kv.Key}", memo.Sha256(t.At(kv.Key)), S(kv.Value))
                   ?? Same("memo again", memo.Sha256(t.At(kv.Key)), S(kv.Value)) ?? Same("memo absent", memo.Sha256(t.At("absent")), null);
        }));
        Case("toolidentity package", () => Rows(D(tree["package"]), kv =>
            Same(kv.Key, PyJson.Dumps(t.Mask(ToolIdentity.Package(t.At(kv.Key))), indent: 1), PyJson.Dumps(kv.Value, indent: 1))));
        Case("toolidentity probe_cmd", () =>
        {
            string before = Environment.CurrentDirectory;
            Environment.CurrentDirectory = t.Root;
            try
            {
                return Rows(L(tree["probeCmd"]).Select(D), row =>
                    Same(Show(S(row["text"])), PyJson.Dumps(t.Mask(ToolIdentity.ProbeCmd(S(row["text"])))), PyJson.Dumps(row["identity"])));
            }
            finally { Environment.CurrentDirectory = before; }
        });
        Case("toolidentity differences", () => Rows(L(tree["differences"]).Select(D), row =>
            Same("lines", string.Join(" | ", ToolIdentity.Differences(D(PyJson.Loads(S(row["old"]))), D(PyJson.Loads(S(row["new"]))))),
                 string.Join(" | ", Strings(row["out"])))));
        Case("toolidentity short", () => Rows(L(tree["short"]).Select(D), row =>
            Same(S(row["src"]), ToolIdentity.Short(PyJson.Loads(S(row["src"]))), S(row["out"]))));
        Case("controller identity is this build folder's package", () =>
        {
            var id = ToolIdentity.Controller();
            var files = L(id["files"]).Select(f => S(D(f)["path"])).ToList();
            if (S(id["kind"]) != "package" || S(id["state"]) != "present" || !S(id["digest"]).StartsWith("folder:")) return "not a present package";
            if (!files.Contains("runcontrol.dll") || !files.Contains("housecarl-core.dll")) return "runcontrol.dll or housecarl-core.dll is not in it";
            if (files.Any(f => f.EndsWith(".pdb"))) return "a .pdb is in it";
            return ToolIdentity.DriverSha256() == S(L(id["files"]).Select(D).First(f => S(f["path"]) == "runcontrol.dll")["sha256"])
                ? null : "the driver hash is not runcontrol.dll's";
        });
    }

    const string AtShape = @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\+00:00$";

    // The same document without its "at" fields; a wrongly shaped "at" is reported.
    static object? WithoutAt(object? v, List<string> bad)
    {
        if (v is List<object?> l) return l.Select(x => WithoutAt(x, bad)).ToList();
        if (v is not PyDict d) return v;
        var copy = new PyDict();
        foreach (var kv in d)
        {
            if (kv.Key != "at") copy[kv.Key] = WithoutAt(kv.Value, bad);
            else if (kv.Value is not string s || !Regex.IsMatch(s, AtShape)) bad.Add(PyText.Repr(kv.Value));
        }
        return copy;
    }

    static void LedgerCases(PyDict ka)
    {
        var want = D(ka["ledger"]);
        using var t = new Temp();
        BuildTree(D(ka["tree"]), t);
        PyDict in1 = D(want["in1"]), in2 = D(want["in2"]), extra = D(want["extra"]);
        string run = t.At("run");
        Directory.CreateDirectory(run);
        string[] outs = [t.At("notes.txt"), t.At("sub")];
        var said = new List<string>();
        string? sentence = null;
        Case("stage ledger: the scripted sequence runs", () =>
        {
            // the same steps, in the same order, as ledger_case in make_known_answers.py
            var ledger = new StageLedger(run);
            said.Add(ledger.Decide("census", in1));
            ledger.Record("census", in1, outs, 0, extra);
            said.Add(ledger.Decide("census", in1));
            File.WriteAllBytes(t.At("notes.txt"), "changed"u8.ToArray());
            said.Add(ledger.Decide("census", in1));
            ledger.Record("census", in1, outs, 0, extra);
            said.Add(ledger.Decide("census", in1));
            SetMtimeNs(t.At("sub/b.txt"), 1_700_000_000_000_077_700);
            said.Add(ledger.Decide("census", in1));
            var reused = new PyDict(extra) { ["seconds"] = 0.0, ["reusedFrom"] = "C:/runs/earlier", ["reusedSeconds"] = null };
            ledger.Record("census", in1, outs, 0, reused);
            ledger.Record("census", in2, [], 2);
            D(L(ledger.Doc["history"])[^1])["reason"] = "refused: why it stopped";
            ledger.Save();
            said.Add(ledger.Decide("census", in2));
            File.WriteAllBytes(t.At("notes.txt"), "changed again"u8.ToArray());
            said.Add(ledger.Decide("census", in1));
            File.WriteAllBytes(t.At("notes.txt"), "changed"u8.ToArray());
            said.Add(ledger.Decide("census", in1));
            ledger.Record("scope", in2, [t.At("sub")], 0, extra);
            try { ledger.Record("plan", in1, [t.At("missing.json"), t.At("notes.txt"), t.At("gone/x")], 0); }
            catch (Refusal r) { sentence = r.Message; }
            ledger = new StageLedger(run);
            said.Add(ledger.Decide("scope", in2));
            ledger.Record("scope", in2, [t.At("sub")], 3);
            said.Add(ledger.Decide("plan", in1));
            said.Add(ledger.Decide("scope", in1));
            ledger.Record("scope", in1, [t.At("sub")], 0, extra);
            said.Add(ledger.Decide("scope", in2));
            said.Add(ledger.Decide("scope", in1));
            return null;
        });
        Case("stage ledger: decisions", () => Same("decisions", string.Join(" ", said), string.Join(" ", Strings(want["decisions"]))));
        Case("stage ledger: exit 0 with a missing output is refused", () => Same("sentence", sentence == null ? null : t.Mask(sentence), (string?)want["refusal"]));
        Case("stage ledger: stages.json is Python's, apart from its times", () =>
        {
            var bad = new List<string>();
            object? mine = t.Mask(WithoutAt(PyJson.ReadPipelineFile(PyPath.Join(run, "stages.json")), bad));
            if (bad.Count > 0) return "an 'at' field has another shape: " + string.Join(", ", bad);
            return Same("document", PyJson.Dumps(mine, indent: 2, ensureAscii: false), S(want["stages"]));
        });
        Case("stage outcome and run status", () =>
            Rows(L(ka["outcomes"]).Select(D), row =>
            {
                var a = L(row["args"]);
                return Same(S(a[0]), PyJson.Dumps(Stages.Outcome(S(a[0]), (int)(long)a[1]!, (bool)a[2]!, S(a[3]))), PyJson.Dumps(row["out"]));
            })
            ?? Rows(L(ka["runStatus"]).Select(D), row =>
                Same("status", Stages.RunStatus(Strings(row["statuses"]).Select(s => new PyDict { ["status"] = s }).ToList()), S(row["out"]))));
    }

    static void EntryCases(PyDict ka)
    {
        var want = D(ka["entry"]);
        var doc = D(want["entry"]);
        var entry = new Entry(doc);
        Case("entry: the stage table is Python's stage_entry_keys for the ported stages", () =>
            Same("table", PyJson.Dumps(new PyDict(Stages.Ported.Select(s => new KeyValuePair<string, object?>(s, Stages.PresentationReads[s].Cast<object?>().ToList())))),
                 PyJson.Dumps(want["reads"]))
            ?? Same("presentation keys", string.Join(",", PyOrder.SortedByCodePoint(Stages.PresentationKeys)), string.Join(",", Strings(want["presentationKeys"])))
            ?? Same("non-blocking", string.Join(",", PyOrder.SortedByCodePoint(Stages.NonBlocking)), string.Join(",", Strings(want["nonBlocking"]))));
        Case("entry: stage_entry_hash per stage", () =>
            Rows(D(want["hashes"]), kv => Same(kv.Key, entry.StageEntryHash(kv.Key), S(kv.Value)))
            ?? Rows(D(want["budgetHashes"]), kv => Same($"budget {kv.Key}", entry.StageEntryHash(kv.Key, D(want["budgetMeasured"])), S(kv.Value))));
        Case("entry: a declared presentation read is served", () =>
            Same("modNames", PyJson.Dumps(entry.Presentation("bundle", "modNames")), PyJson.Dumps(doc["modNames"]))
            ?? Same("a measuring key", PyJson.Dumps(entry.Get("selection")), PyJson.Dumps(doc["selection"])));
        foreach (var (name, read, stage, key) in new (string, Action, string, string)[]
                 {
                     ("an undeclared presentation read", () => entry.Presentation("census", "modNames"), "census", "modNames"),
                     ("a presentation key the stage does not declare", () => entry.Presentation("bundle", "patchSearch"), "bundle", "patchSearch"),
                     ("a presentation key read without a stage", () => entry.Get("modNames"), "", "modNames"),
                     ("a measuring key read as presentation", () => entry.Presentation("bundle", "selection"), "", "selection"),
                     ("a stage outside the table", () => entry.Presentation("report-plan", "modNames"), "report-plan", "modNames"),
                     ("an entry hash for a stage outside the table", () => entry.StageEntryHash("report-plan"), "report-plan", ""),
                 })
            Case($"entry: refuses {name}", () =>
            {
                try { read(); }
                catch (Refusal r) { return r.Message.Contains(stage) && r.Message.Contains(key) ? null : $"the refusal does not name the stage and key: {r.Message}"; }
                return "nothing was refused";
            });
    }

    // ---- against a live Python, when one is on PATH

    static (int exit, string stdout, string stderr)? Python(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("python")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.ArgumentList.Add("-X");
        psi.ArgumentList.Add("utf8");
        foreach (string a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, stdout.TrimEnd(), err.Result.TrimEnd());
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    // The last line Python printed, or the reason there is none.
    static string? LastLine((int exit, string stdout, string stderr)? r, out string? why)
    {
        why = r == null ? "python did not start" : r.Value.exit != 0 ? $"python exited {r.Value.exit}: {r.Value.stderr}" : null;
        return why != null ? null : PyText.SplitLines(r!.Value.stdout).LastOrDefault() ?? "";
    }

    // ToolIdentity (C#) against _geometry-harness/toolidentity.py, the Python copy SkyWright's Python tools use (the
    // two stage-ledger cross-checks with the module's pipeline moved to the module's tests, StageLedgerSeamTests)
    static void LiveCases()
    {
        string[] names = ["live: toolidentity.package of real build folders", "live: toolidentity.probe_cmd with an interpreter on PATH"];
        string? root = HarnessShared.CheckoutLayout.Locate(null, out _)?.Checkout;
        string harness = root == null ? "" : Path.Combine(root, "_geometry-harness");
        string? reason = !File.Exists(Path.Combine(harness, "toolidentity.py")) ? "this build is not inside a SkyWright checkout with its Python tools"
            : Python(harness, "--version") is not { exit: 0 } ? "python is not on PATH" : null;
        if (reason != null) { foreach (string n in names) Skip(n, reason); return; }
        const string head = "import sys, json; sys.path.insert(0, sys.argv[1]); ";

        Case(names[0], () =>
        {
            string[] folders = [AppContext.BaseDirectory, Path.GetDirectoryName(Checkout.ScopeprobeExe)!, Path.Combine(harness, "no-such-build")];
            return Rows(folders, f =>
            {
                string? line = LastLine(Python(harness, "-c", head + "import toolidentity; print(json.dumps(toolidentity.package(sys.argv[2])))", harness, f), out var why);
                return why ?? Same(f, PyJson.Dumps(ToolIdentity.Package(f)), line);
            });
        });
        Case(names[1], () =>
        {
            string text = "python " + Path.Combine(harness, "toolidentity.py") + " _shared/Configuration.cs";
            string before = Environment.CurrentDirectory;
            Environment.CurrentDirectory = harness;
            try
            {
                string? line = LastLine(Python(harness, "-c", head + "import toolidentity; print(json.dumps(toolidentity.probe_cmd(sys.argv[2])))", harness, text), out var why);
                return why ?? Same("identity", PyJson.Dumps(ToolIdentity.ProbeCmd(text)), line);
            }
            finally { Environment.CurrentDirectory = before; }
        });
    }
}
