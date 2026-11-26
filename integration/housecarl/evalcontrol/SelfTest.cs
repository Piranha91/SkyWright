using System.IO.Compression;
using System.Reflection;
using System.Text;
using RunControl;

namespace EvalControl;

/// <summary>`evalcontrol --self-test`: the known answers of the rules that moved here from runcontrol (the media rule,
/// the record import, the live switch check), each under the name it had there (known-answers/*.json, moved unchanged; the Python controller recorded them). The helpers are the
/// test harness's, not a rule. Prints one GREEN, RED or SKIP line per case and a total; exit 0 when nothing is red.</summary>
public static partial class SelfTest
{
    static int _green, _red;

    static void Case(string name, Func<string?> body)
    {
        string? why;
        try { why = body(); }
        catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message} at {string.Join(" / ", (ex.StackTrace ?? "").Split('\n').Skip(1).Take(4).Select(x => x.Trim()))}"; }
        if (why == null) { _green++; Console.WriteLine($"GREEN {name}"); }
        else { _red++; Console.WriteLine($"RED {name}: {why}"); }
    }

    public static int Run()
    {
        Checkout.UseBuild(Program.AppBuild());
        var media = Load("media.json");
        MediaRuleCases(D(media["rule"]));
        foreach (var c in L(D(media["commands"])["cases"]).Select(D)) MediaCommandCase(c, D(media["commands"]));
        ImportCases(Load("import.json"));
        RecordsVerifyCases(Load("records-verify.json"));
        LiveCases(Load("live.json"));
        AdviceCases();
        AdviceGenericCases(Load("advice.json"));
        DiscoveryCases(Load("discovery.json"));
        EvaluateCases(Load("evaluate.json"));
        ImportedEvidenceCases(Load("finalize-imports.json"));
        LedgerScriptCases(Load("ledger.json"));
        ValidateCases(Load("validate.json"));
        AiEvaluationCases();
        Console.WriteLine($"{_green} green, {_red} red, 0 skipped");
        return _red == 0 ? 0 : 1;
    }

    // ---- reading the answers

    static PyDict Load(string resource)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return D(PyJson.Loads(r.ReadToEnd()));
    }

    static PyDict D(object? v) => (PyDict)v!;
    static List<object?> L(object? v) => (List<object?>)v!;
    static string S(object? v) => (string)v!;
    static List<string> Strings(object? v) => L(v).Select(S).ToList();

    static byte[] Inflate(string b64)
    {
        using var z = new ZLibStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
        using var plain = new MemoryStream();
        z.CopyTo(plain);
        return plain.ToArray();
    }

    static string? Unpack(object? v) => v is PyDict d ? Encoding.UTF8.GetString(Inflate(S(d["z"]))) : (string?)v;

    static string? Same(string what, string? got, string? want)
    {
        if (got == want) return null;
        int at = 0;
        while (got != null && want != null && at < got.Length && at < want.Length && got[at] == want[at]) at++;
        string Around(string? s) => s == null ? "null" : PyText.Repr(s.Substring(Math.Max(0, at - 40), Math.Min(120, Math.Max(0, s.Length - Math.Max(0, at - 40)))));
        return $"{what}: differs at {at}: got {Around(got)}, Python has {Around(want)}";
    }

    static string? SameJson(string what, object? got, object? want) => Same(what, PyJson.Dumps(got), PyJson.Dumps(want));

    static string? Rows(IEnumerable<object?> rows, Func<PyDict, string?> check)
    {
        var bad = new List<string>();
        int n = 0;
        foreach (var row in rows.Select(D))
        {
            n++;
            string? why;
            try { why = check(row); }
            catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message} at {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"; }
            if (why != null) bad.Add($"row {n}: {why}");
        }
        return bad.Count == 0 ? null : $"{bad.Count} of {n} rows differ; " + string.Join(" | ", bad.Take(3));
    }

    static T In<T>(string folder, Func<T> step)
    {
        string before = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(folder);
        try { return step(); }
        finally { Directory.SetCurrentDirectory(before); }
    }

    static (int exit, string stdout, string stderr) Captured(Func<int> step)
    {
        var (o, e) = (Console.Out, Console.Error);
        var (so, se) = (new StringWriter(), new StringWriter());
        Console.SetOut(so);
        Console.SetError(se);
        try { return (step(), so.ToString().Replace("\r\n", "\n"), se.ToString().Replace("\r\n", "\n")); }
        catch (Refusal r) { se.WriteLine($"REFUSED: {r.Message}"); return (Exit.Refused, so.ToString().Replace("\r\n", "\n"), se.ToString().Replace("\r\n", "\n")); }
        finally
        {
            Console.SetOut(o);
            Console.SetError(e);
        }
    }

    sealed class Temp : IDisposable
    {
        public string Base { get; }
        public string Root { get; }
        public Temp(string? sub = null)
        {
            string made = Path.Combine(Path.GetTempPath(), "hc-evalcontrol-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(made);
            Base = PyPath.Resolve(made);
            Root = sub == null ? Base : PyPath.Join(Base, sub);
            Directory.CreateDirectory(Root);
        }
        public string At(string rel) => rel == "." ? Root : PyPath.Join(Root, rel);
        public string Unmask(string s) => s.Replace(@"<ROOT\\>", Root.Replace(@"\", @"\\")).Replace(@"<ROOT\>", Root).Replace("<ROOT/>", PyOrder.Fwd(Root));
        public string? Mask(string? s) => s?.Replace(Root.Replace(@"\", @"\\"), @"<ROOT\\>").Replace(Root, @"<ROOT\>").Replace(PyOrder.Fwd(Root), "<ROOT/>");
        public object? Unmask(object? v) => v switch
        {
            string s => Unmask(s),
            PyDict d => new PyDict(d.Select(kv => new KeyValuePair<string, object?>(Unmask(kv.Key), Unmask(kv.Value)))),
            List<object?> l => l.Select(Unmask).ToList(),
            _ => v,
        };
        public string Write(string rel, byte[] data)
        {
            string p = At(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
            return p;
        }
        public void Dispose()
        {
            try { Directory.Delete(Base, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static byte[] RowBytes(PyDict row, Temp t) =>
        row.ContainsKey("text") ? PyJson.Utf8(t.Unmask(Unpack(row["text"])!))
        : row["bytes"] is PyDict z ? Inflate(S(z["z"])) : Convert.FromBase64String(S(row["bytes"]));

    static void WritePlanSha(Temp t) =>
        File.WriteAllBytes(t.At("plan.sha256"), PyJson.Utf8(Hashing.Sha256File(t.At("plan.json")) + "\n"));

    static void BuildTree(Temp t, object? tree)
    {
        foreach (var row in L(tree).Select(D).Where(r => !r.ContainsKey("planSha"))) t.Write(S(row["path"]), RowBytes(row, t));
        if (L(tree).Select(D).Any(r => r.ContainsKey("planSha"))) WritePlanSha(t);
    }

    // the recorded changes to a copy of a base folder, made again here (runcontrol's self-test makes the same)
    static void ApplyOps(Temp t, object? ops)
    {
        foreach (var op in L(ops is string s ? PyJson.Loads(s) : ops).Select(D))
        {
            string path = op.ContainsKey("path") ? t.At(S(op["path"])) : "";
            switch (S(op["op"]))
            {
                case "delete": File.Delete(path); break;
                case "rmtree": Directory.Delete(path, recursive: true); break;
                case "write": t.Write(S(op["path"]), PyJson.Utf8(t.Unmask(Unpack(op["text"])!))); break;
                case "planSha": WritePlanSha(t); break;
                case "copytree":
                    foreach (string f in Directory.GetFiles(t.At(S(op["from"])), "*", SearchOption.AllDirectories))
                        t.Write(S(op["path"]) + "/" + PyPath.RelativeTo(f, t.At(S(op["from"]))), File.ReadAllBytes(f));
                    break;
                case "json":
                    object? doc = PyJson.LoadEvalFile(path);
                    foreach (var e in L(t.Unmask(op["edits"])).Select(L))
                    {
                        var steps = L(e[1]);
                        object? at = doc;
                        for (int i = 0; i < (S(e[0]) == "append" ? steps.Count : steps.Count - 1); i++)
                            at = steps[i] is string k ? ((PyDict)at!)[k] : ((List<object?>)at!)[(int)(long)steps[i]!];
                        if (S(e[0]) == "append") ((List<object?>)at!).Add(e[2]);
                        else if (S(e[0]) == "pop") ((PyDict)at!).Pop(S(steps[^1]));
                        else if (steps[^1] is string key) ((PyDict)at!)[key] = e[2];
                        else ((List<object?>)at!)[(int)(long)steps[^1]!] = e[2];
                    }
                    PyJson.WriteEvalFile(path, doc);
                    break;
                default: throw new ArgumentException($"no recorded change '{S(op["op"])}' in a moved case");
            }
        }
    }

    // ---- the media rule (moved from runcontrol's self-test with the rule; names as there)

    static void MediaRuleCases(PyDict ka)
    {
        var vocabulary = D(ka["vocabulary"]);
        Case("bundle: the media vocabularies", () =>
            SameJson("kinds", MediaRule.Kinds.Cast<object?>().ToList(), vocabulary["kinds"])
            ?? SameJson("sources", MediaRule.FingerprintSources.Cast<object?>().ToList(), vocabulary["sources"])
            ?? SameJson("reasons", MediaRule.Reasons.Cast<object?>().ToList(), vocabulary["reasons"]));
        static Dictionary<string, HashSet<string>> Keyed(object? d) =>
            D(d).ToDictionary(kv => PyValue.Hash(kv.Key), kv => L(kv.Value).Select(PyValue.Hash).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        Case("bundle: media_item_refusal", () =>
        {
            using var t = new Temp(".");
            BuildTree(t, ka["tree"]);
            var states = D(ka["states"]);
            HashSet<string> State(string arm, object? state) =>
                Ev.Items(states.Get($"{arm[1..]}|{Ev.Str(state)}")).Select(x => (string)x!).ToHashSet(StringComparer.Ordinal);
            return In(t.Root, () => Rows(L(ka["cases"]), row =>
            {
                var item = D(row["item"]);
                var (reason, detail) = MediaRule.Refusal(item, PyPath.Str(PyValue.Truthy(item.Get("path")) ? S(item["path"]) : ""),
                    Keyed(ka["fingerprints"]), Keyed(ka["others"]), State);
                if (reason != null && !MediaRule.Reasons.Contains(reason)) return $"a reason outside the vocabulary: {reason}";
                return Same("reason", reason, (string?)row["reason"]) ?? Same("detail", detail, (string?)row["detail"]);
            }));
        });
        Case("bundle: state_media_fingerprints", () => Rows(L(ka["stateFingerprints"]), row =>
            SameJson("fingerprints", MediaRule.StateFingerprints(L(row["configs"]).Select(D), Strings(row["measured"]).ToHashSet(StringComparer.Ordinal), row["state"])
                .OrderBy(x => x, StringComparer.Ordinal).Cast<object?>().ToList(), row["want"])));
    }

    // ---- bundle --media, as evalcontrol bundles it: the recorded run is the app scan; its bundle is made by
    // runcontrol's own bundle; the overlay is planned, evaluated and finalized over it; the media file the recording
    // passed to runcontrol bundle --media goes to evalcontrol bundle --media. What the recording judged of each item
    // (bundled as evidence with its row, or unassessed with its reason, or the media file refused) must be judged again.

    static void MediaCommandCase(PyDict c, PyDict ka) =>
        Case($"bundle: the command on '{S(c["name"])}'", () =>
        {
            using var t = new Temp("b");
            BuildTree(t, ka["base"]);
            ApplyOps(t, c["ops"]);
            // the recorded plan is a hand-made one with no options block, which every plan runcontrol makes carries; the
            // overlay's evaluate runs runcontrol's executor, which reads it, so the plan gets the options of this build
            var recordedPlan = D(PyJson.LoadEvalFile(t.At("plan.json")));
            recordedPlan["options"] = new PyDict { ["harness"] = Checkout.Harness, ["probeCmd"] = new PyDict() };
            // ...and no configuration block, which the executor's accounting receipt reads; the scope's stands in
            var scopeDoc = Ev.OrEmpty(PyJson.LoadEvalFile(t.At(Ev.Str(Ev.G(recordedPlan["scope"], "path")))));
            recordedPlan["configuration"] = Py.Copy(Ev.OrEmpty(scopeDoc.Get("configuration")));
            // ...and it records registry version 7, newer than the app's, which verify-bundle refuses: by content it is a
            // run made before the AI-side rows left the registry, so it is labelled older (4), as runcontrol's own
            // synthetic bases are (the registry copy's bytes, and so the hash the plan froze, are left as recorded)
            if (!Ev.Eq(D(recordedPlan["registry"])["registryVersion"], 7L)) return "the recorded plan no longer records registry version 7";
            D(recordedPlan["registry"])["registryVersion"] = 4L;
            PyJson.WriteEvalFile(t.At("plan.json"), recordedPlan);
            WritePlanSha(t);
            // the recording's app run credits two record imports (r.contested, adapter kind mcp-import): an app scan
            // holds none since the record checks left SkyWright's registry, so their entries, receipts and copied
            // evidence are taken out and runcontrol finalizes the run again before it is bundled
            var ledgerDoc = D(PyJson.LoadEvalFile(t.At("ledger.json")));
            var imported = L(recordedPlan["checks"]).Select(D).Where(ch => Ev.Eq(Ev.G(ch["adapter"], "kind"), RecordEvidence.ImportKind))
                .Select(ch => S(ch["checkId"])).ToHashSet(StringComparer.Ordinal);
            var entries = D(ledgerDoc["entries"]);
            foreach (string key in entries.Keys.Where(k => imported.Contains(S(D(entries[k])["checkId"]))).ToList())
            {
                entries.Pop(key);
                File.Delete(t.At($"receipts/{key}.json"));
                if (Directory.Exists(t.At($"imports/{key}"))) Directory.Delete(t.At($"imports/{key}"), recursive: true);
            }
            PyJson.WriteEvalFile(t.At("ledger.json"), ledgerDoc);
            var (fin, _, finErr) = In(t.Root, () => Captured(() => Finalizer.Run(t.Root)));
            if (fin is not (0 or 3)) return $"the app run did not finalize again (exit {fin}): {finErr.Trim()}";
            var args = Strings(c["args"]);
            string media = args[args.IndexOf("--media") + 1];
            string appBundle = PyPath.Join(t.Base, "app-bundle"), ai = PyPath.Join(t.Base, "ai"), aiBundle = PyPath.Join(t.Base, "ai-bundle");
            var steps = new (string what, Func<int> step)[]
            {
                ("the app bundle", () => Bundle.Run(new BundleArgs { Run = t.Root, Out = appBundle, NoNames = true })),
                // the media rows of the shipped list (a case about media plans the media group alone)
                ("the overlay plan", () => OverlayPlan.Run(t.Root, appBundle, ai, [], MediaList(t.Base))),
                ("evaluate", () => OverlayEvaluate.Run(ai)),
                ("finalize", () => Finalizer.Run(ai, OverlayFinalize.Amend, ModuleRows.LaunchKinds, OverlayFinalize.Sections, AiList.NeverCompletingKinds)),
            };
            foreach (var (what, step) in steps)
            {
                var (code, _, err) = In(t.Root, () => Captured(step));
                if (code != Exit.Ok) return $"{what} did not succeed (exit {code}): {err.Trim()}";
            }
            var (exit, _, stderr) = In(t.Root, () => Captured(() => OverlayBundle.Run(ai, aiBundle, media, null, null, null)));
            long want = (long)c["exit"]!;
            if (want != Exit.Ok)
                return Same("exit", exit.ToString(), want.ToString())
                    ?? Same("the refusal", t.Mask(stderr.Split('\n')[0]), t.Mask(S(c["stderr"]).Split('\n').First(l => l.StartsWith("REFUSED:", StringComparison.Ordinal))));
            if (exit != Exit.Ok) return $"exit {exit}: {stderr.Trim()}";
            var recorded = D(PyJson.Loads(Unpack(L(c["tree"]).Select(D).First(r => S(r["path"]) == "manifest.json")["text"])!));
            var got = D(PyJson.LoadEvalFile(PyPath.Join(aiBundle, "manifest.json")));
            static List<object?> MediaRows(PyDict m) => Ev.Items(m.Get("evidence")).Where(e => Ev.Eq(Ev.G(e, "kind"), "media")).ToList();
            string? why = SameJson("media evidence rows", t.Mask(MediaRows(got)), MediaRows(recorded))
                ?? SameJson("unassessed media", t.Mask(got.Get("unassessedMedia")), recorded.Get("unassessedMedia"));
            if (why != null) return why;
            foreach (var e in MediaRows(got).Select(D))
                if (Hashing.Sha256FileOrNull(PyPath.Join(aiBundle, S(e["path"]))) != S(e["sha256"])) return $"{S(e["path"])} does not hash to its row";
            // the bundle verifies with the app bundle it is bound to
            var (v, vout, verr) = Captured(() => OverlayVerify.Run(aiBundle, null, null));
            return v == Exit.Ok ? null : $"the overlay bundle does not verify (exit {v}): {vout.Trim()} {verr.Trim()}";
        });

    // The shipped list with only its media rows and their families, written beside the case's folders.
    static string MediaList(string dir)
    {
        var list = D(PyJson.LoadEvalFile(AiList.Shipped));
        var rows = L(list["checks"]).Select(D).Where(c => Ev.Eq(Ev.G(c["adapter"], "kind"), "media")).ToList();
        var families = rows.Select(c => S(c["family"])).ToHashSet(StringComparer.Ordinal);
        list["checks"] = rows.Cast<object?>().ToList();
        list["families"] = L(list["families"]).Where(f => families.Contains(S(D(f)["id"]))).ToList();
        string path = PyPath.Join(dir, AiList.FileName);
        PyJson.WriteEvalFile(path, list);
        return path;
    }

    static object? Mask(this Temp t, object? v) => v switch
    {
        string s => t.Mask(s),
        PyDict d => new PyDict(d.Select(kv => new KeyValuePair<string, object?>(t.Mask(kv.Key)!, t.Mask(kv.Value)))),
        List<object?> l => l.Select(x => t.Mask(x)).ToList(),
        _ => v,
    };
}
