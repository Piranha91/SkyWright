using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace RunControl;

/// <summary>The known answers for `runcontrol ordersweep`: row identities, emit reading, the diff text, the
/// classification, the memory route, the candidate files, the program key, the cache, the refusal marker, the
/// arguments and the request key. Every expected value was computed by ordersweep.py
/// (known-answers/make_known_answers_sweep.py) and this program must reach it exactly. No probe and no Python runs here.</summary>
public static class SelfTestSweep
{
    static Action<string, Func<string?>> Case = (_, _) => { };

    static PyDict D(object? v) => (PyDict)v!;
    static List<object?> L(object? v) => (List<object?>)v!;
    static string S(object? v) => (string)v!;
    static List<string> Strings(object? v) => L(v).Select(S).ToList();
    static string Show(string? s) => s == null ? "null" : PyText.Repr(s.Length > 200 ? s[..200] + "..." : s);

    static string? Same(string what, string? got, string? want)
    {
        if (got == want) return null;
        int at = 0;
        while (got != null && want != null && at < got.Length && at < want.Length && got[at] == want[at]) at++;
        string Around(string? s) => s == null ? "null" : Show(s[Math.Max(0, Math.Min(at, s.Length) - 40)..Math.Min(s.Length, at + 80)]);
        return $"{what}: differs at {at}: got {Around(got)}, Python has {Around(want)}";
    }

    // Two values as the same JSON text, key order included.
    static string? SameJson(string what, object? got, object? want) => Same(what, PyJson.Dumps(got), PyJson.Dumps(want));

    // A dict as [key, value] rows, the way the answers keep an order.
    static List<object?> Pairs(PyDict d) => d.Select(kv => (object?)new List<object?> { kv.Key, kv.Value }).ToList();

    // The first few rows of a table that do not hold, as one sentence; null when all hold.
    static string? Rows(IEnumerable<PyDict> rows, Func<PyDict, string?> check)
    {
        var bad = new List<string>();
        int n = 0;
        foreach (var row in rows)
        {
            n++;
            string? why;
            try { why = check(row); }
            catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message}"; }
            if (why != null) bad.Add($"row {n} ({row.Get("name") ?? row.Get("key") ?? row.Get("job") ?? ""}): {why}");
        }
        return n == 0 ? "the table is empty" : bad.Count == 0 ? null : $"{bad.Count} of {n} rows differ; " + string.Join(" | ", bad.Take(3));
    }

    // A temporary folder named the way Python's resolve() would name it; the answers name it by a placeholder.
    sealed class Temp : IDisposable
    {
        public string Root { get; }
        public Temp()
        {
            string made = Path.Combine(Path.GetTempPath(), "hc-runcontrol-sweep-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(made);
            Root = PyPath.Resolve(made);
        }
        public string At(string rel) => PyPath.Join(Root, rel);
        public string Unmask(string s) => s.Replace(@"<ROOT\\>", Root.Replace(@"\", @"\\")).Replace(@"<ROOT\>", Root).Replace("<ROOT/>", PyOrder.Fwd(Root));
        public string Mask(string s) => s.Replace(Root.Replace(@"\", @"\\"), @"<ROOT\\>").Replace(Root, @"<ROOT\>").Replace(PyOrder.Fwd(Root), "<ROOT/>");
        public object? Mask(object? v) => v switch
        {
            string s => Mask(s),
            PyDict d => new PyDict(d.Select(kv => new KeyValuePair<string, object?>(Mask(kv.Key), Mask(kv.Value)))),
            List<object?> l => l.Select(Mask).ToList(),
            _ => v,
        };
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
        public string Write(string rel, string text) => Write(rel, PyJson.Utf8(text));
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // What a step printed, with "\n" line ends.
    static string Printed(Action step)
    {
        var before = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { step(); }
        finally { Console.SetOut(before); }
        return captured.ToString().Replace("\r\n", "\n");
    }

    static string ReadLog(string path) => File.Exists(path) ? Encoding.UTF8.GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n") : "";

    public static void Run(Action<string, Func<string?>> @case)
    {
        Case = @case;
        PyDict ka;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("sweep.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            ka = D(PyJson.Loads(r.ReadToEnd()));

        Case("sweep: the sentence and the default probes", () =>
            Same("NOT_PROOF", OrderSweep.NotProof, S(ka["notProof"]))
            ?? Same("default probes", new SweepArgs().Probes, string.Join(",", Strings(ka["defaultProbes"]))));
        Case("sweep: row_identity", () => Rows(L(ka["rowIdentity"]).Select(D), row =>
        {
            var a = L(row["args"]);
            return Same(PyJson.Dumps(a), OrderSweep.RowIdentity(S(a[0]), S(a[1]), (string?)a[2], S(a[3]), a[4]), S(row["want"]));
        }));
        Case("sweep: parse_diff", () => Rows(L(ka["parseDiff"]).Select(D), row =>
            SameJson("parsed", OrderSweep.ParseDiff(S(row["text"])), row["want"])));
        Case("sweep: classify", () => Rows(L(ka["classify"]).Select(D), row =>
            Same("word", OrderSweep.Classify(L(row["rows"]).Select(D).ToList()), S(row["want"]))));
        EmitCases(ka);
        MemoryCases(L(ka["memoryRoute"]).Select(D).ToList());
        CandidateCases(L(ka["candidateFiles"]).Select(D).ToList());
        ProgramKeyCases(D(ka["programKey"]));
        CacheCases(D(ka["cache"]));
        Case("sweep: the refusal marker over the harness's refusal and count lines", () => Rows(L(ka["refused"]).Select(D), row =>
            OrderSweep.RefusedRe.IsMatch(S(row["text"])) == (bool)row["want"]! ? null : $"{Show(S(row["text"]))} read {!(bool)row["want"]!}, Python has {row["want"]}"));
        ArgumentCases(L(ka["arguments"]).Select(D).ToList());
        Case("sweep: the request key of every job of one Python sweep", () => Rows(L(ka["requestKeys"]).Select(D), row =>
            Same("key", OrderSweep.RequestKey(S(row["probe"]), S(row["program"]), S(row["orderSha"]), (string?)row["baseSha"], Strings(row["arguments"]),
                                              (string?)row["populationSha"], (string?)row["reverseOfSha"]), S(row["want"]))));
    }

    // ---- emits

    static void EmitCases(PyDict ka)
    {
        using var t = new Temp();
        string At(string name) => t.At($"emits/{name}.json");
        var emits = L(ka["emits"]).Select(D).ToList();
        foreach (var e in emits)
            if (e["text"] is string text) t.Write($"emits/{S(e["name"])}.json", text);
        Case("sweep: emit_rows", () => Rows(emits, e => SameJson("rows", Pairs(OrderSweep.EmitRows(At(S(e["name"])))), e["rows"])));
        Case("sweep: emit_predicates", () => Rows(emits, e => SameJson("predicates", Pairs(OrderSweep.EmitPredicates(At(S(e["name"])))), e["predicates"])));
        Case("sweep: read_identity", () => Rows(emits, e => SameJson("identity", OrderSweep.ReadIdentity(At(S(e["name"]))), e["identity"])));
        Case("sweep: created_breakdown", () => Rows(L(ka["created"]).Select(D), row =>
            SameJson("breakdown", OrderSweep.CreatedBreakdown(At(S(row["base"])), At(S(row["candidate"])), row["reverse"] is string r ? At(r) : null), row["want"])));
    }

    // ---- the memory route

    static void MemoryCases(List<PyDict> rows)
    {
        using var t = new Temp();
        Case("sweep: memory_route", () => Rows(rows, row =>
        {
            string? emit = row["emit"] is string rel ? t.Write(rel, "{}") : null;
            if (row["sidecar"] is string side) t.Write(S(row["emit"]) + ".memory.json", side);
            var got = OrderSweep.MemoryRoute(emit);
            if (got.ContainsKey("sidecarUnreadable")) got["sidecarUnreadable"] = "<the runtime's own words>";
            return SameJson("route", t.Mask(got), row["want"]);
        }));
    }

    // ---- the candidate files

    static void CandidateCases(List<PyDict> rows)
    {
        using var t = new Temp();
        Case("sweep: candidate_files", () => Rows(rows, row =>
        {
            string folder = t.At(S(row["folder"]));
            foreach (string f in Strings(row["files"])) t.Write(S(row["folder"]) + "/" + f, "{}");
            var want = D(row["want"]);
            try
            {
                var (baseline, others) = OrderSweep.CandidateFiles(folder, row["baseline"] is string b ? t.Unmask(b) : null,
                                                                   row["limit"] is { } n ? (System.Numerics.BigInteger?)PyValue.Int(n) : null);
                var got = new PyDict
                {
                    ["baseline"] = t.Mask(baseline), ["others"] = others.Select(o => (object?)t.Mask(o)).ToList(),
                    ["stems"] = others.Select(o => (object?)SweepFiles.Stem(o)).ToList(),
                };
                return SameJson("files", got, want);
            }
            catch (SweepExit e)
            {
                return want.ContainsKey("exit") ? Same("the stop", t.Mask(e.Message), S(want["exit"])) ?? (e.Code == 1 ? null : $"exit code {e.Code}, Python has 1")
                    : $"stopped with {Show(e.Message)}, Python has {PyJson.Dumps(want)}";
            }
        }));
    }

    // ---- the program key

    static void ProgramKeyCases(PyDict ka)
    {
        using var t = new Temp();
        var none = new Dictionary<string, string>();
        Case("sweep: program_key of a Release folder", () => Rows(L(ka["folders"]).Select(D).Select((row, i) => (row, i)).Select(x =>
        {
            foreach (var f in L(x.row["files"]).Select(D))
                t.Write($"keys/{x.i}/fakeprobe/bin/Release/net9.0/{S(f["path"])}", Convert.FromBase64String(S(f["bytes"])));
            x.row["harness"] = t.At($"keys/{x.i}");
            return x.row;
        }), row => Same("digest", OrderSweep.ProgramKey(S(row["harness"]), "fakeprobe", none), S(row["want"]))));
        Case("sweep: program_key of a stand-in program", () =>
        {
            var un = D(ka["unverifiable"]);
            string? why = Same("a command naming no file", OrderSweep.ProgramKey(".", "fakeprobe", new() { ["fakeprobe"] = S(un["command"]) }), S(un["want"]));
            if (why != null) return why;
            // a changed script under the same command text is another program
            string script = t.Write("keys/fake.py", "print(1)");
            var over = new Dictionary<string, string> { ["fakeprobe"] = "no-such-program-on-path-7f3a " + script };
            string k1 = OrderSweep.ProgramKey(".", "fakeprobe", over);
            t.Write("keys/fake.py", "print(2)");
            string k2 = OrderSweep.ProgramKey(".", "fakeprobe", over);
            return !k1.StartsWith("probe-cmd:", StringComparison.Ordinal) || k1.StartsWith("probe-cmd:unverifiable:", StringComparison.Ordinal)
                ? $"a command naming a script is keyed {k1}" : k1 == k2 ? "the same command text over a changed script gave one key" : null;
        });
    }

    // ---- the cache

    static void CacheCases(PyDict ka)
    {
        using var t = new Temp();
        string root = "cache/";
        var ownDoc = (object?)null;
        foreach (var f in L(ka["files"]).Select(D))
        {
            if (f.ContainsKey("json"))
            {
                if (S(f["path"]) == S(ka["own"])) ownDoc = f["json"];
                t.Write(root + S(f["path"]), PyJson.Dumps(t.Unmask(f["json"]), indent: 1));
            }
            else t.Write(root + S(f["path"]), S(f["text"]));
        }
        string own = t.At(root + S(ka["own"])), log = t.At(root + S(ka["log"]));
        var prior = Strings(ka["prior"]).Select(p => t.At(root + p)).ToList();
        void Reset(string ownText)
        {
            File.WriteAllBytes(own, PyJson.Utf8(ownText));
            File.Delete(log);
        }
        string ownText = PyJson.Dumps(t.Unmask(ownDoc), indent: 1);

        Case("sweep: Cache.lookup", () => Rows(L(ka["lookups"]).Select(D), row =>
        {
            Reset(ownText);
            (string emit, string cacheFile)? hit = null;
            string printed = Printed(() => hit = new SweepCache(own, log, prior).Lookup(S(row["key"]), S(row["label"])));
            object? got = hit is { } h ? new List<object?> { t.Mask(h.emit), t.Mask(h.cacheFile) } : null;
            return SameJson("served", got, row["want"]) ?? Same("printed", t.Mask(printed), S(row["stdout"])) ?? Same("cache-log.txt", t.Mask(ReadLog(log)), S(row["log"]));
        }));
        Case("sweep: Cache.record", () => Rows(L(ka["records"]).Select(D), row =>
        {
            Reset(ownText);
            SweepCache? cache = null;
            string printed = Printed(() =>
            {
                cache = new SweepCache(own, log, prior);
                cache.Record(S(row["key"]), S(row["label"]), t.At(root + S(row["emit"])));
            });
            object? entry = cache!.Entries.Get(S(row["key"]));
            if (entry is PyDict e) entry = new PyDict(e) { ["recordedUtc"] = "<TIME>" };
            var saved = D(PyJson.LoadEvalFile(own));
            return SameJson("entry", entry, row["entry"]) ?? SameJson("saved keys", saved.Keys.Cast<object?>().ToList(), row["savedKeys"])
                ?? Same("printed", t.Mask(printed), S(row["stdout"])) ?? Same("cache-log.txt", t.Mask(ReadLog(log)), S(row["log"]));
        }));
        Case("sweep: the bytes a cache save writes", () =>
        {
            Reset("{}");
            var first = L(ka["records"]).Select(D).First();
            Printed(() => new SweepCache(own, log, prior).Record(S(first["key"]), "job-identity", t.At(root + S(first["emit"]))));
            string text = Regex.Replace(Encoding.UTF8.GetString(File.ReadAllBytes(own)), "\"recordedUtc\": \"[^\"]*\"", "\"recordedUtc\": \"<TIME>\"");
            return Same("cache.json", text, S(ka["savedText"]));
        });
    }

    // ---- the arguments

    static void ArgumentCases(List<PyDict> rows)
    {
        Case("sweep: the arguments, as argparse reads them", () => Rows(rows.Select(r => { r["name"] = PyJson.Dumps(r["argv"]); return r; }), row =>
        {
            var argv = Strings(row["argv"]).ToArray();
            var want = D(row["want"]);
            SweepArgs a;
            try { a = SweepArgs.Parse(argv); }
            catch (SweepExit e)
            {
                const string head = "runcontrol ordersweep: error: ";
                string last = e.Message.Split('\n')[^1];
                if (!want.ContainsKey("error")) return $"stopped with {Show(last)}, Python read the values";
                return e.Code != 2 ? $"exit code {e.Code}, argparse exits 2" : Same("the error", last.StartsWith(head, StringComparison.Ordinal) ? last[head.Length..] : last, S(want["error"]));
            }
            if (!want.ContainsKey("values")) return $"read the values, Python stopped with {Show(S(want["error"]))}";
            var values = D(want["values"]);
            var got = new PyDict
            {
                ["candidates"] = a.Candidates, ["out"] = a.Out, ["probes"] = a.Probes, ["instance"] = a.Instance, ["profile"] = a.Profile, ["game"] = a.Game,
                ["probe_args"] = a.ProbeArgs, ["harness"] = a.Harness, ["baseline"] = a.Baseline,
                ["max_candidates"] = a.MaxCandidates is { } n ? PyValue.Whole(n) : null, ["diff_tolerance"] = a.DiffTolerance, ["no_reverse"] = a.NoReverse,
                ["no_pair_results"] = a.NoPairResults, ["full_reverse"] = a.FullReverse, ["deadline_seconds"] = a.DeadlineSeconds,
                ["probe_cmd"] = a.ProbeCmd.Cast<object?>().ToList(), ["population"] = a.Population, ["population_probes"] = a.PopulationProbes,
                ["reuse_cache"] = a.ReuseCache.Cast<object?>().ToList(),
            };
            if (!values.ContainsKey("harness")) got.Pop("harness");
            return SameJson("values", got, values);
        }));
    }
}
