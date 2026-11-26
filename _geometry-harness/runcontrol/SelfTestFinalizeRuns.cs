using System.IO.Compression;
using System.Text;

namespace RunControl;

// The known answers that need a run folder or a bundle on disk: ordersweep's classification word, the disposition and
// STATUS.md of synthetic run folders, the finalize command, and verify-bundle on synthetic bundles.
public static partial class SelfTestFinalize
{
    // A long text the answers keep deflated ({"z": base64}), or the text itself.
    static string? Unpack(object? v) => v is PyDict d ? Encoding.UTF8.GetString(Inflate(S(d["z"]))) : (string?)v;

    static byte[] Inflate(string b64)
    {
        using var z = new ZLibStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
        using var plain = new MemoryStream();
        z.CopyTo(plain);
        return plain.ToArray();
    }

    // One row of a recorded tree as the bytes to write under this root.
    static byte[] RowBytes(PyDict row, Temp t) =>
        row.ContainsKey("text") ? PyJson.Utf8(t.Unmask(Unpack(row["text"])!))
        : row["bytes"] is PyDict z ? Inflate(S(z["z"])) : Convert.FromBase64String(S(row["bytes"]));

    static void WritePlanSha(Temp t) =>
        File.WriteAllBytes(t.At("plan.sha256"), PyJson.Utf8(Hashing.Sha256File(t.At("plan.json")) + "\n"));

    // The recorded folder rebuilt under this root; a planSha row is the hash of the plan as written here.
    static void BuildTree(Temp t, object? tree)
    {
        foreach (var row in L(tree).Select(D).Where(r => !r.ContainsKey("planSha"))) t.Write(S(row["path"]), RowBytes(row, t));
        if (L(tree).Select(D).Any(r => r.ContainsKey("planSha"))) WritePlanSha(t);
    }

    // The generator's recorded changes to a copy of a base folder, made again here.
    static void ApplyOps(Temp t, object? ops)
    {
        foreach (var op in L(ops).Select(D))
        {
            string path = op.ContainsKey("path") ? t.At(S(op["path"])) : "";
            switch (S(op["op"]))
            {
                case "delete": File.Delete(path); break;
                case "rmtree": Directory.Delete(path, recursive: true); break;
                case "write": t.Write(S(op["path"]), PyJson.Utf8(t.Unmask(Unpack(op["text"])!))); break;
                case "planSha": WritePlanSha(t); break;
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
                default: throw new ArgumentException($"no recorded change '{S(op["op"])}'");
            }
        }
    }

    // What a step printed to standard output and standard error, with "\n" line ends, and its exit code.
    static (int exit, string stdout, string stderr) Captured(Func<int> step)
    {
        var (o, e) = (Console.Out, Console.Error);
        var (so, se) = (new StringWriter(), new StringWriter());
        Console.SetOut(so);
        Console.SetError(se);
        try { return (step(), so.ToString().Replace("\r\n", "\n"), se.ToString().Replace("\r\n", "\n")); }
        finally
        {
            Console.SetOut(o);
            Console.SetError(e);
        }
    }

    // One step with the run-log store named, so a run folder under test does not become the store.
    static T StoreKept<T>(string store, Func<T> step)
    {
        string? before = Environment.GetEnvironmentVariable("HOUSECARL_RUNLOG_DIR");
        Environment.SetEnvironmentVariable("HOUSECARL_RUNLOG_DIR", string.IsNullOrWhiteSpace(before) ? store : before);
        try { return step(); }
        finally { Environment.SetEnvironmentVariable("HOUSECARL_RUNLOG_DIR", before); }
    }

    static List<object?> Pairs(PyDict d) => d.Select(kv => (object?)new List<object?> { kv.Key, kv.Value }).ToList();

    // historicalClassifications in one fixed order: Python leaves equal sort keys in set order.
    static object? TieSorted(object? word)
    {
        if (word is not PyDict d || d.Get("historicalClassifications") is not List<object?> h) return word;
        var copy = new PyDict(d);
        copy["historicalClassifications"] = h.OrderBy(PyJson.Canonical, PyOrder.CodePoint).ToList();
        return copy;
    }

    static partial void Later(PyDict ka)
    {
        ClassificationCases(D(ka["classification"]));
        DispositionCases(D(ka["dispositions"]));
        var bases = D(D(ka["dispositions"])["bases"]);
        foreach (var c in L(ka["finalize"]).Select(D)) FinalizeCase(c, bases);
        var verify = D(ka["verify"]);
        foreach (var c in L(verify["cases"]).Select(D)) VerifyCase(c, verify["base"]);
        NewerRegistryCase(verify["base"]);
    }

    // Worked by hand from the rule: the verify base (an older bundle, holding receipts of kinds this SkyWright does not
    // check) with its plan re-labelled one version past this SkyWright's and every hash of the plan carried (plan.sha256,
    // the manifest's two rows), so the version is the only change: refused, exit 3, exactly one defect, on plan.json,
    // naming both versions; nothing is listed as not checked and nothing is mismatched. The wrong answer it rejects: a
    // newer SkyWright's bundle accepted, or its rows judged or listed by this one's rules (test_verify_old_kinds.py V6
    // is the same rule on the record-rows fixture).
    static void NewerRegistryCase(object? baseTree) =>
        Case("verify-bundle: a plan of a registry newer than this SkyWright's is refused, its rows neither judged nor listed", () =>
        {
            using var t = new Temp("b");
            BuildTree(t, baseTree);
            var plan = D(PyJson.LoadEvalFile(t.At("plan.json")));
            long newer = VerifyBundle.CurrentRegistryVersion + 1;
            D(plan["registry"])["registryVersion"] = newer;
            PyJson.WriteEvalFile(t.At("plan.json"), plan);
            WritePlanSha(t);
            var manifest = D(PyJson.LoadEvalFile(t.At("manifest.json")));
            int carried = 0;
            foreach (var e in L(manifest["evidence"]).Select(D))
                if (S(e["path"]) is "plan.json" or "plan.sha256")
                {
                    e["sha256"] = Hashing.Sha256File(t.At(S(e["path"])));
                    e["bytes"] = new FileInfo(t.At(S(e["path"]))).Length;
                    carried++;
                }
            PyJson.WriteEvalFile(t.At("manifest.json"), manifest);
            if (carried != 2) return $"the base's manifest lists {carried} of plan.json and plan.sha256, not 2";
            var (exit, stdout, _) = Captured(() => VerifyBundle.Run(t.Root));
            var bundle = D(D(PyJson.LoadEvalFile(t.At("verify.json")))["bundle"]);
            var defects = L(bundle["defects"]).Select(D).ToList();
            string want = $"the plan records registry version {newer}, newer than this SkyWright's {VerifyBundle.CurrentRegistryVersion}, "
                + "so a newer SkyWright made this bundle; check it with that SkyWright's runcontrol verify-bundle.";
            return Same("exit", exit.ToString(), "3")
                ?? Same("defects", string.Join("; ", defects.Select(d => $"{S(d["kind"])} on {Ev.Str(d["path"])}: {S(d["detail"])}")),
                    $"registry-version-newer on plan.json: {want}")
                ?? (bundle.ContainsKey("notChecked") ? "verify.json lists rows as not checked" : null)
                ?? (stdout.Contains("does not check", StringComparison.Ordinal) ? "the not-checked sentence was printed" : null)
                ?? (L(bundle["mismatched"]).Count + L(bundle["missing"]).Count > 0 ? "a file is mismatched or missing: the re-label was not carried" : null);
        });

    static void ClassificationCases(PyDict ka)
    {
        Case("finalize: order_classification", () =>
        {
            using var t = new Temp();
            BuildTree(t, ka["tree"]);
            return Rows(L(ka["cases"]), row =>
            {
                var rows = L(row["rows"]).Select(D).ToList();
                object? got = t.Mask(SweepReading.OrderClassification(t.Root, D(ka["arm"]), rows));
                // only the tie case is compared as a set: every other history has one order
                bool tie = S(row["name"]).Contains("(a tie)");
                return SameJson("word", tie ? TieSorted(got) : got, tie ? TieSorted(row["want"]) : row["want"]);
            });
        });
        Case("finalize: read_sweep_jobs", () =>
        {
            using var t = new Temp();
            BuildTree(t, ka["tree"]);
            return Rows(L(ka["jobs"]), row =>
                SameJson(S(row["folder"]), t.Mask(Pairs(SweepReading.ReadSweepJobs(t.At("sweep/" + S(row["folder"]))))), row["want"]));
        });
        Case("finalize: the launch folder name", () => Rows(L(ka["folders"]), row =>
            Same(S(row["name"]), SweepReading.SweepFolderRe.IsMatch(S(row["name"])).ToString(), ((bool)row["want"]!).ToString())));
    }

    static void DispositionCases(PyDict ka)
    {
        var bases = D(ka["bases"]);
        foreach (var c in L(ka["cases"]).Select(D))
            Case($"finalize: disposition and STATUS.md of '{S(c["name"])}'", () =>
            {
                using var t = new Temp("b");
                BuildTree(t, bases[S(c["base"])]);
                ApplyOps(t, c["ops"]);
                var plan = D(PyJson.LoadEvalFile(t.At("plan.json")));
                return Clocked(() =>
                {
                    var ledger = new Ledger(t.Root, plan);
                    var receipts = Receipts.ReadReceipts(t.Root);
                    var (integrity, bad) = EvidenceIntegrity.Check(t.Root, plan, ledger, receipts);
                    var disp = Disposition.Build(t.Root, plan, ledger, receipts, integrity, bad);
                    return Same("integrity", t.Mask(PyJson.Dumps(integrity, indent: 1, ensureAscii: false)), Unpack(c["integrity"]))
                        ?? SameJson("bad keys", Pairs(bad), c["bad"])
                        ?? Same("disposition", t.Mask(PyJson.Dumps(disp, indent: 1, ensureAscii: false)), Unpack(c["disposition"]))
                        ?? Same("STATUS.md", t.Mask(StatusMd.Build(plan, disp)), Unpack(c["status"]));
                });
            });
    }

    static string? FileText(string path) => File.Exists(path) ? Encoding.UTF8.GetString(File.ReadAllBytes(path)) : null;

    static void FinalizeCase(PyDict c, PyDict bases) =>
        Case($"finalize: the command on '{S(c["name"])}'", () =>
        {
            using var t = new Temp("b");
            BuildTree(t, bases[S(c["base"])]);
            ApplyOps(t, c["ops"]);
            foreach (string n in new[] { "disposition.json", "STATUS.md" }) File.Delete(t.At(n));
            var (exit, stdout, stderr) = StoreKept(PyPath.Join(t.Base, "runlog"),
                () => Clocked(() => In(t.Root, () => Captured(() => Finalizer.Run(t.Root)))));
            var files = D(c["files"]);
            return Same("exit", exit.ToString(), ((long)c["exit"]!).ToString())
                ?? Same("stdout", t.Mask(stdout), S(c["stdout"])) ?? Same("stderr", t.Mask(stderr), S(c["stderr"]))
                ?? Same("disposition.json", t.Mask(FileText(t.At("disposition.json"))), Unpack(files["disposition.json"]))
                ?? Same("STATUS.md", t.Mask(FileText(t.At("STATUS.md"))), Unpack(files["STATUS.md"]));
        });

    static void VerifyCase(PyDict c, object? baseTree) =>
        Case($"verify-bundle: '{S(c["name"])}'", () =>
        {
            using var t = new Temp("b");
            BuildTree(t, baseTree);
            ApplyOps(t, c["ops"]);
            string? target = c["out"] is true ? PyPath.Join(t.Base, "verify-out.json") : null;
            var (exit, stdout, stderr) = Clocked(() => Captured(() => VerifyBundle.Run(t.Root, target)));
            return Same("exit", exit.ToString(), ((long)c["exit"]!).ToString())
                ?? Same("stdout", t.Mask(stdout), S(c["stdout"])) ?? Same("stderr", t.Mask(stderr), S(c["stderr"]))
                ?? Same("verify.json", t.Mask(FileText(target ?? t.At("verify.json"))), Unpack(c["verify"]))
                ?? Same("wrote inside", File.Exists(t.At("verify.json")).ToString(), ((bool)c["wroteInside"]!).ToString())
                ?? (Captured(() => VerifyBundle.Run(t.Root, PyPath.Join(t.Base, "quiet.json"), quiet: true)) is var q
                    && q.exit == exit && q.stdout.Length == 0 && q.stderr.Length == 0 ? null : "the quiet form printed or gave another exit code");
        });
}
