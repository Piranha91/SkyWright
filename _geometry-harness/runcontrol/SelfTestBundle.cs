using System.Reflection;
using System.Text;

namespace RunControl;

// The known answers for `runcontrol bundle`: every expected value was computed by evalrun.py and bundleenrich.py
// (known-answers/make_known_answers_bundle.py). The record reader's conversations are replayed from the recording, so
// no Python and no built server runs here.
public static partial class SelfTestFinalize
{
    /// <summary>A recorded conversation played back: what the client sends must be the recorded bytes in the recorded
    /// order; what it reads is what the scripted reader wrote, and nothing once the recording ends.</summary>
    sealed class ReplayWire(List<object?> rows) : IReaderWire
    {
        int _at;
        public string? Wrong { get; private set; }
        public bool Finished => _at == rows.Count;
        public PyDict? Env { get; init; }

        public void Send(string text)
        {
            if (_at < rows.Count && D(rows[_at]).Get("sent") is string want)
            {
                if (want != text) Wrong ??= Same($"request {_at + 1}", text, want);
                _at++;
            }
            else Wrong ??= $"the client sent a message the recording does not hold at row {_at + 1}: {Show(text)}";
        }

        public string? ReadLine() => _at < rows.Count && D(rows[_at]).Get("recv") is string line && ++_at > 0 ? line : null;

        public void Close() { }
    }

    // One step with an environment variable set.
    static T WithEnv<T>(string name, string? value, Func<T> step)
    {
        string? before = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try { return step(); }
        finally { Environment.SetEnvironmentVariable(name, before); }
    }

    // The generator's recorded changes; a copied folder is the one change ApplyOps does not know.
    static void ApplyBundleOps(Temp t, object? ops)
    {
        foreach (var op in L(ops).Select(D))
        {
            if (S(op["op"]) != "copytree") { ApplyOps(t, new List<object?> { op }); continue; }
            foreach (string f in Directory.GetFiles(t.At(S(op["from"])), "*", SearchOption.AllDirectories))
                t.Write(S(op["path"]) + "/" + PyPath.RelativeTo(f, t.At(S(op["from"]))), File.ReadAllBytes(f));
        }
    }

    public static void RunBundle(Action<string, Func<string?>> @case)
    {
        Case = @case;
        PyDict ka;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("bundle.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            ka = D(PyJson.Loads(r.ReadToEnd()));
        var commands = D(ka["commands"]);
        foreach (var c in L(commands["cases"]).Select(D)) BundleCommandCase(c, commands);
        OrderChoiceCases(L(ka["order"]));
        FileVocabularyCases(D(ka["files"]));
        TextRuleCases(D(ka["text"]));
        WalkCases(D(ka["walk"]));
        PremiseSourceCases(D(ka["premises"]));
    }

    // A names sentence up to the reader's own words, which are the Python runtime's and cannot match.
    static string? UpToRuntimeText(string? s)
    {
        if (s == null) return null;
        int at = s.IndexOf("): ", StringComparison.Ordinal);
        return at < 0 ? s : s[..(at + 3)];
    }

    static void BundleCommandCase(PyDict c, PyDict ka) =>
        Case($"bundle: the command on '{S(c["name"])}'", () =>
        {
            using var t = new Temp("b");
            BuildTree(t, ka["base"]);
            ApplyBundleOps(t, c["ops"]);
            var args = new List<string> { "bundle", "--run", "." };
            args.AddRange(Strings(c["args"]));
            ReplayWire? wire = null;
            var (open, tool, timer) = (ReaderClient.Open, EnrichCommit.Tool, BundleSteps.Timer);
            ReaderClient.Open = (cmd, env) => cmd.StartsWith("python ", StringComparison.Ordinal) ? wire = new ReplayWire(L(c["wire"])) { Env = env } : open(cmd, env);
            EnrichCommit.Tool = () => L(ka["tool"]);
            BundleSteps.Timer = () => () => 3.25;
            int exit;
            string stdout, stderr;
            try
            {
                (exit, stdout, stderr) = WithEnv("HOUSECARL_NEXUS_CATALOG_DIR", S(ka["catalogue"]), () => StoreKept(PyPath.Join(t.Base, "runlog"),
                    () => Clocked(() => In(t.Root, () => Captured(() => EvalCommands.Run(args.ToArray()))))));
            }
            finally { (ReaderClient.Open, EnrichCommit.Tool, BundleSteps.Timer) = (open, tool, timer); }
            bool runtimeText = c["runtimeText"] is true, intended = c["intended"] != null;
            string? Text(string? s) => runtimeText ? UpToRuntimeText(s) : s;
            string? why = Same("exit", exit.ToString(), ((long)c["exit"]!).ToString())
                ?? Same("stdout", t.Mask(stdout), S(c["stdout"])) ?? Same("stderr", Text(t.Mask(stderr)), Text(S(c["stderr"])))
                ?? wire?.Wrong ?? (wire is { Finished: false } ? "the conversation stopped before the recording's end" : null)
                ?? (wire == null && L(c["wire"]).Count > 0 ? "the recording holds a conversation and no reader was started" : null);
            if (why != null) return why;
            if (wire?.Env is { } env && !args.Contains("--reader-env"))
            {
                // the default instance: the four variables, and the instance built before the reader starts
                if (string.Join(",", env.Keys) != "HOUSECARL_DATA_DIR,HouseCarl__DataDir,HouseCarl__ModsDir,HouseCarl__ProfileDir") return $"reader environment: {string.Join(",", env.Keys)}";
            }
            else if (wire?.Env is { } given && PyJson.Dumps(given) != "{\"HouseCarl__DataDir\": \"names-instance/Data\", \"HOUSECARL_DATA_DIR\": \"names-instance/data\"}")
                return $"reader environment: {PyJson.Dumps(given)}";
            string outDir = Path.GetFullPath(Path.Combine(t.Root, S(c["out"])));
            if (c["tree"] == null) return Directory.Exists(outDir) ? "an out folder was written where Python wrote none" : null;
            var want = L(c["tree"]).Select(D).ToList();
            var gotFiles = Directory.Exists(outDir)
                ? Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Select(f => PyPath.RelativeTo(f, outDir)!).OrderBy(x => x, StringComparer.Ordinal).ToList()
                : [];
            if (intended)
            {
                // Python derives the reversed setup's rows here; this controller says it does not
                var order = D(D(PyJson.LoadEvalFile(Path.Combine(outDir, "manifest.json")))["order"]);
                return Same("order status", (string?)order["status"], "refused")
                    ?? (S(order["sentence"]).Contains(S(c["intended"]), StringComparison.Ordinal) ? null : $"order sentence: {Show(S(order["sentence"]))}")
                    ?? (gotFiles.Any(f => f.StartsWith("enrichment/", StringComparison.Ordinal)) ? "an enrichment was written" : null);
            }
            why = Same("files", string.Join("\n", gotFiles), string.Join("\n", want.Select(r => S(r["path"])).OrderBy(x => x, StringComparer.Ordinal)));
            if (why != null) return why;
            foreach (var row in want)
            {
                string rel = S(row["path"]), file = Path.Combine(outDir, rel);
                if (row.ContainsKey("text"))
                {
                    string text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
                    if (runtimeText && rel == "manifest.json") continue;   // its names sentence holds the runtime's words
                    why = Same(rel, rel == "verify.json" ? t.Mask(text) : text, Unpack(row["text"]));
                }
                else why = Same(rel + " sha256", Hashing.Sha256File(file), S(row["sha256"]));
                if (why != null) return why;
                if (row.Get("linked") is true)
                {
                    // an import's file is a hard link to the run's own
                    var a = NativeFile.Identity(file);
                    var b = NativeFile.Identity(t.At(rel["evidence/".Length..]));
                    if (a == null || b == null || a.Value.index != b.Value.index) return $"{rel}: Python linked it to the run's file; here it is a copy";
                }
                else if (rel.StartsWith("evidence/imports/", StringComparison.Ordinal)
                         && NativeFile.Identity(file) is { } a && NativeFile.Identity(t.At(rel["evidence/".Length..])) is { } b && a.index == b.index)
                    return $"{rel}: runcontrol's own bundle links no folder; here it is a hard link to the run's file";
            }
            return null;
        });

    static void OrderChoiceCases(List<object?> rows) =>
        Case("bundle: order_choice and is_master_file", () => Rows(rows, row =>
        {
            var pair = Strings(row["pair"]);
            var heads = new Dictionary<string, OrderChoice.Head>(StringComparer.Ordinal);
            foreach (var (k, h) in D(row["heads"]))
                heads[k] = new OrderChoice.Head(Ev.Items(D(h).Get("masters")).Select(Ev.Str).ToList(), D(h).Get("master"));
            var got = OrderChoice.Decide(pair[0], pair[1], heads);
            var want = D(row["want"]);
            var doc = new PyDict
            {
                ["pair"] = row["pair"], ["forced"] = got.Forced, ["rule"] = got.Rule, ["loadsFirst"] = got.LoadsFirst,
                ["missing"] = got.Missing == null ? new List<object?>() : new List<object?> { got.Missing },
            };
            return SameJson("choice", doc, want)
                ?? SameJson("is master file", pair.Select(p => (object?)OrderChoice.IsMasterFile(p, heads.GetValueOrDefault(PyText.Lower(p)))).ToList(), row["isMaster"]);
        }));

    // the bundle's own vocabularies (the media rule's moved to the houseCARL integration module with the media row)
    static void FileVocabularyCases(PyDict ka) =>
        Case("bundle: the binary suffixes and the control files", () =>
            SameJson("binary suffixes", Bundle.BinarySuffixes.OrderBy(x => x, StringComparer.Ordinal).Cast<object?>().ToList(), ka["binarySuffixes"])
            ?? SameJson("control files", Bundle.ControlFiles.Cast<object?>().ToList(), ka["controlFiles"]));

    static void TextRuleCases(PyDict ka)
    {
        Case("bundle: the reader command line's split", () => Rows(L(ka["split"]), row =>
        {
            object? got;
            try { got = PyShell.ReaderArgv(S(row["line"])).Cast<object?>().ToList(); }
            catch (PyValueError ex) { got = "ValueError: " + ex.Message; }
            return SameJson(Show(S(row["line"])), got, row["want"]);
        }));
        Case("bundle: float()", () => Rows(L(ka["floats"]), row =>
        {
            string got;
            try { got = PyText.FloatRepr(PyShell.Float(row["value"])); }
            catch (PyValueError ex) { got = "ValueError: " + ex.Message; }
            return Same(PyJson.Dumps(row["value"]), got, row.ContainsKey("error") ? "ValueError: " + S(row["error"]) : S(row["want"]));
        }));
        Case("bundle: --mod-name and --reader-env pairs", () =>
            Rows(L(ka["modNames"]), row => SameJson("mod names", Pairs(BundleSteps.ModNames(Strings(row["pairs"]))), row["want"]))
            ?? Rows(L(ka["readerEnv"]), row => SameJson("reader env", Pairs(BundleSteps.ReaderEnv(Strings(row["pairs"]))), row["want"])));
    }

    static List<object?> SortedPairs<T>(IEnumerable<KeyValuePair<string, T>> rows) =>
        rows.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (object?)new List<object?> { kv.Key, kv.Value is double[] p ? p.Cast<object?>().ToList() : kv.Value }).ToList();

    static void WalkCases(PyDict ka)
    {
        Case("bundle: the plugin walk (filed_cells, _records, _position, cell_overrides)", () =>
        {
            using var t = new Temp();
            BuildTree(t, ka["tree"]);
            var basePositions = PluginWalk.OwnPositions(new PluginWalk.Plugin(t.At("Base.esm")));
            return Rows(L(ka["files"]), row =>
            {
                var p = new PluginWalk.Plugin(t.At(S(row["file"])));
                var over = new PyDict();
                foreach (var (cell, _) in D(row["overrides"]))
                    over[cell] = PluginWalk.CellOverrides(p, cell, fk => basePositions.GetValueOrDefault(PyText.Lower(fk)));
                return SameJson("names", p.Names.Cast<object?>().ToList(), row["names"])
                    ?? SameJson("filed cells", Pairs(PluginWalk.FiledCells(p, Strings(ka["fks"]))), row["filed"])
                    ?? SameJson("own positions", SortedPairs(PluginWalk.OwnPositions(p)),
                        L(row["own"]).OrderBy(x => S(L(x)[0]), StringComparer.Ordinal).ToList())
                    ?? SameJson("cell overrides", over, row["overrides"]);
            });
        });
        var instance = D(ka["instance"]);
        Case("bundle: private_instance", () =>
        {
            using var t = new Temp();
            var env = PrivateInstance.Create(t.Root);
            string? why = SameJson("environment", t.Mask(Pairs(env)), instance["env"]);
            foreach (var f in L(instance["tree"]).Select(D))
                why ??= SameBytes(S(f["path"]), File.ReadAllBytes(t.At(S(f["path"]))), RowBytes(f, t));
            return why ?? Same("file count", Directory.GetFiles(t.Root, "*", SearchOption.AllDirectories).Length.ToString(), L(instance["tree"]).Count.ToString());
        });
    }

    static void PremiseSourceCases(PyDict ka) =>
        Case("bundle: bundle_premise_sources", () =>
        {
            using var t = new Temp();
            BuildTree(t, ka["tree"]);
            return In(t.Root, () => Rows(L(ka["cases"]), row =>
            {
                var named = D(row["named"]);
                var (sources, bad, code) = Bundle.PremiseSources(D(row["plan"]),
                    new[] { "registry", "scope", "search" }.ToDictionary(k => k, k => (string?)named.Get(k)));
                return SameJson("sources", sources.Select(kv => (object?)new List<object?> { kv.Key, kv.Value }).ToList(), row["sources"])
                    ?? Same("refusal", t.Mask(bad), (string?)row["bad"]) ?? Same("code", code.ToString(), ((long)row["code"]!).ToString());
            }));
        });
}
