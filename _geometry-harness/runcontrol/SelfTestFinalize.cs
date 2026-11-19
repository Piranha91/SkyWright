using System.Reflection;
using System.Text;

namespace RunControl;

/// <summary>The known answers for the run-folder model and for finalize and verify-bundle. Every expected value was
/// computed by evalrun.py (known-answers/make_known_answers_finalize.py) and this program must reach it exactly.
/// No probe and no Python runs here.</summary>
public static partial class SelfTestFinalize
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

    static string? SameBytes(string what, byte[] got, byte[] want) =>
        got.AsSpan().SequenceEqual(want) ? null : Same(what, Encoding.UTF8.GetString(got), Encoding.UTF8.GetString(want)) ?? $"{what}: the bytes differ";

    // The first few rows of a table that do not hold, as one sentence; null when all hold.
    static string? Rows(IEnumerable<object?> rows, Func<PyDict, string?> check)
    {
        var bad = new List<string>();
        int n = 0;
        foreach (var row in rows.Select(D))
        {
            n++;
            string? why;
            try { why = check(row); }
            catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message}"; }
            if (why != null) bad.Add($"row {n} ({row.Get("name") ?? ""}): {why}");
        }
        return n == 0 ? "the table is empty" : bad.Count == 0 ? null : $"{bad.Count} of {n} rows differ; " + string.Join(" | ", bad.Take(3));
    }

    // A temporary folder named the way Python's resolve() would name it; the answers name it by a placeholder.
    sealed class Temp : IDisposable
    {
        public string Root { get; }
        public Temp()
        {
            string made = Path.Combine(Path.GetTempPath(), "hc-runcontrol-finalize-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(made);
            Root = PyPath.Resolve(made);
        }
        public string At(string rel) => rel == "." ? Root : PyPath.Join(Root, rel);
        public string Unmask(string s) => s.Replace(@"<ROOT\\>", Root.Replace(@"\", @"\\")).Replace(@"<ROOT\>", Root).Replace("<ROOT/>", PyOrder.Fwd(Root));
        public string? Mask(string? s) => s?.Replace(Root.Replace(@"\", @"\\"), @"<ROOT\\>").Replace(Root, @"<ROOT\>").Replace(PyOrder.Fwd(Root), "<ROOT/>");
        public object? Mask(object? v) => v switch
        {
            string s => Mask(s),
            PyDict d => new PyDict(d.Select(kv => new KeyValuePair<string, object?>(Mask(kv.Key)!, Mask(kv.Value)))),
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
        // The answers' folder rebuilt: each file's bytes, or its text with the placeholder replaced by this root.
        public void Build(object? tree, object? dirs = null)
        {
            foreach (var f in L(tree).Select(D))
                Write(S(f["path"]), f.ContainsKey("text") ? PyJson.Utf8(Unmask(S(f["text"]))) : Convert.FromBase64String(S(f["bytes"])));
            foreach (string d in dirs == null ? [] : Strings(dirs)) Directory.CreateDirectory(At(d));
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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

    // One step with the process's current folder set, as the generator's cwd().
    static T In<T>(string folder, Func<T> step)
    {
        string before = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(folder);
        try { return step(); }
        finally { Directory.SetCurrentDirectory(before); }
    }

    static string? Sha(string path) => File.Exists(path) ? Hashing.Sha256Bytes(File.ReadAllBytes(path)) : null;

    public static void Run(Action<string, Func<string?>> @case)
    {
        Case = @case;
        PyDict ka;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("finalize.json")!)
        using (var r = new StreamReader(s, Encoding.UTF8))
            ka = D(PyJson.Loads(r.ReadToEnd()));
        PlanCases(D(ka["plans"]));
        LedgerCases(D(ka["ledger"]));
        ReceiptCases(D(ka["receipts"]));
        IdentityCases(D(ka["identity"]));
        Later(ka);
    }

    static partial void Later(PyDict ka);

    // ---- plan loading and its refusals

    static void PlanCases(PyDict ka)
    {
        Case("model: load_plan", () => Rows(L(ka["load"]), row =>
        {
            using var t = new Temp();
            t.Build(row["tree"]);
            var (plan, why) = EvalStore.LoadPlan(t.Root);
            return Same("loaded", (plan != null).ToString(), ((bool)row["plan"]!).ToString()) ?? Same("why", t.Mask(why), (string?)row["why"]);
        }));
        Case("model: request_refusal", () => Rows(L(ka["request"]), row =>
            Same("refusal", EvalStore.RequestRefusal(D(row["plan"])), (string?)row["want"])));
        Case("model: refusal_cause", () => Rows(L(ka["cause"]), row =>
            Same(Show((string?)row["why"]), EvalStore.RefusalCause((string?)row["why"]), S(row["want"]))));
        Case("model: identity_contract_refusal", () => Rows(L(ka["contract"]), row =>
            Same("refusal", IdentityChecks.ContractRefusal(D(row["plan"])), (string?)row["want"])));
        var premise = D(ka["premise"]);
        Case("model: premise_refusal and relative_path_refusal", () =>
        {
            using var t = new Temp();
            t.Build(premise["tree"]);
            return In(t.Root, () => Rows(L(premise["cases"]), row =>
            {
                var plan = D(t.Unmask(row["plan"]));
                var only = row["only"] == null ? null : Strings(row["only"]);
                string? remedy = (string?)row["remedy"];
                string? why = only == null && remedy == null ? Same("premise", t.Mask(EvalStore.PremiseRefusal(plan)), (string?)row["premise"]) : null;
                return why ?? Same("relative", t.Mask(EvalStore.RelativePathRefusal(plan, only, remedy)), (string?)row["relative"]);
            }));
        });
        Case("model: premise_file_refusal with a named file", () =>
        {
            using var t = new Temp();
            t.Build(premise["tree"]);
            return Rows(L(premise["named"]), row => Same(S(row["label"]),
                t.Mask(EvalStore.PremiseFileRefusal(D(t.Unmask(row["plan"])), S(row["label"]), S(row["nxt"]), t.Unmask(S(row["named"])))),
                (string?)row["want"]));
        });
    }

    // ---- the ledger

    static string? K(PyDict kw, string key) => (string?)kw.Get(key);

    static void LedgerCases(PyDict ka)
    {
        foreach (var script in L(ka["scripts"]).Select(D))
            Case($"model: ledger script '{S(script["name"])}'", () =>
            {
                using var t = new Temp();
                var plan = D(script["plan"]);
                Ledger? led = null;
                string? bad = Clocked(() => Rows(L(script["steps"]), step =>
                {
                    var op = L(step["op"]);
                    object? got = null;
                    switch (S(op[0]))
                    {
                        case "write": t.Write(S(op[1]), PyJson.Utf8(S(op[2]))); break;
                        case "new": led = new Ledger(t.Root, plan); got = led.Readable; break;
                        case "refusal": got = t.Mask(led!.Refusal()); break;
                        case "open":
                            led!.OpenInvocation(S(op[1]), (string?)op[2], (string?)op[3], op.Count > 4 ? D(op[4]).Get("budget_minutes") : null);
                            break;
                        case "transition":
                            var kw = op.Count > 5 ? D(op[5]) : new PyDict();
                            led!.Transition(S(op[1]), S(op[2]), (string?)op[3], S(op[4]), cause: K(kw, "cause"), receiptId: K(kw, "receipt_id"),
                                detector: K(kw, "detector"), extra: kw.Get("extra") as PyDict, rejections: kw.Get("rejections") as List<object?>,
                                detail: K(kw, "detail"), nextAction: K(kw, "next_action"));
                            break;
                        case "heal": got = (long)led!.HealInterrupted(); break;
                        case "rebuild": led!.Rebuild(); break;
                        case "entry": got = led!.Entry(op[1], op[2], op[3]) is { } e ? PyJson.Dumps(e) : null; break;
                        case "facts": got = PyJson.Dumps(led!.ReceiptFacts(S(op[1]), D(op[2]))); break;
                        default: return $"no step '{S(op[0])}'";
                    }
                    var files = D(step["files"]);
                    return SameJson($"{S(op[0])} answer", got, step["want"])
                        ?? Same("ledger.json hash", Sha(t.At("ledger.json")), (string?)files["ledger.json"])
                        ?? Same("journal.jsonl hash", Sha(t.At("journal.jsonl")), (string?)files["journal.jsonl"]);
                }));
                if (bad != null) return bad;
                foreach (var f in L(script["tree"]).Select(D))
                {
                    string? why = SameBytes(S(f["path"]), File.ReadAllBytes(t.At(S(f["path"]))), Convert.FromBase64String(S(f["bytes"])));
                    if (why != null) return why;
                }
                return null;
            });
        Case("model: Ledger.key", () => Rows(L(ka["keys"]), row =>
        {
            var a = L(row["args"]);
            return Same(PyJson.Dumps(a), Ledger.Key(a[0], a[1], a[2]), S(row["want"]));
        }));
    }

    // ---- receipts

    static void ReceiptCases(PyDict ka)
    {
        Case("model: write_receipt", () =>
        {
            using var t = new Temp();
            foreach (var f in L(ka["files"]).Select(D))
            {
                string p = t.Write(S(f["path"]), Convert.FromBase64String(S(f["bytes"])));
                File.SetLastWriteTimeUtc(p, new DateTime(DateTime.UnixEpoch.Ticks + (long)f["mtimeNs"]! / 100, DateTimeKind.Utc));
            }
            string run = t.At("run");
            return Clocked(() => Rows(L(ka["calls"]), c =>
            {
                var argv = c["argv"] == null ? null : Strings(t.Unmask(c["argv"]));
                var r = Receipts.WriteReceipt(run, D(ka["plan"]), D(ka["check"]), D(ka["arm"]), (string?)c["part"], c["adapter"], argv,
                    (string?)c["runId"], (string?)c["parent"], (string?)c["started"], (string?)c["ended"], c["exit"],
                    Strings(t.Unmask(c["outputs"])), c["log"] == null ? null : t.Unmask(S(c["log"])), c["consumed"], D(c["validation"]),
                    c["result"], c["extra"] as PyDict, keepSame: c.Get("keepSame") is true);
                return Same("returned", t.Mask(PyJson.Dumps(r, indent: 1, ensureAscii: false)), S(c["returned"]))
                    ?? Same("file", t.Mask(Encoding.UTF8.GetString(File.ReadAllBytes(PyPath.Join(run, "receipts", S(c["key"]) + ".json")))), S(c["file"]));
            }));
        });
        Case("model: same_account", () => Rows(L(ka["same"]), row =>
            Same(PyJson.Dumps(row["new"]), Receipts.SameAccount(row["prev"], D(row["new"])).ToString(), ((bool)row["want"]!).ToString())));
        Case("model: read_receipts order", () =>
        {
            using var t = new Temp();
            var read = D(ka["read"]);
            t.Build(read["tree"], read["dirs"]);
            return SameJson("keys", Receipts.ReadReceipts(t.Root).Keys.Cast<object?>().ToList(), read["keys"])
                ?? SameJson("no run", Receipts.ReadReceipts(t.At("no-such-run")).Keys.Cast<object?>().ToList(), read["none"]);
        });
        Case("model: receipt_roles", () => Rows(L(ka["roles"]), row =>
            SameJson(PyJson.Dumps(row["receipt"]),
                Receipts.ReceiptRoles(D(row["receipt"])).Select(kv => (object?)new List<object?> { kv.Key, kv.Value }).ToList(), row["want"])));
        Case("model: program_label and program_id", () => Rows(L(ka["labels"]), row =>
            Same("label", Receipts.ProgramLabel(S(row["pid"])), S(row["want"])) ?? Same("id", Receipts.ProgramId(row["pid"]), S(row["id"]))));
        Case("model: argv_shape", () =>
        {
            using var t = new Temp();
            Directory.CreateDirectory(t.At("run"));
            return In(t.Root, () => Rows(L(ka["shapes"]), row =>
                SameJson("shape", Receipts.ArgvShape(Strings(t.Unmask(row["argv"])), t.At("run")), row["want"])));
        });
        var deps = D(ka["dependencies"]);
        Case("model: dependency_keys", () => Rows(L(deps["cases"]), row =>
            SameJson(PyJson.Dumps(row["entry"]),
                Receipts.DependencyKeys(D(row["plan"]), D(deps["receipts"]), D(row["entry"])).Cast<object?>().ToList(), row["want"])));
    }

    // ---- identity

    static void IdentityCases(PyDict ka)
    {
        var plans = D(ka["plans"]);
        Case("model: identity_defects", () => Rows(L(ka["cases"]), row =>
            SameJson($"{S(row["plan"])} {PyJson.Dumps(row["receipt"])}",
                IdentityChecks.Defects(D(plans[S(row["plan"])]), D(row["receipt"]))
                    .Select(x => (object?)new List<object?> { x.Role, x.Program, x.Kind, x.Recorded, x.Found }).ToList(), row["want"])));
        Case("model: identity_guarantee", () =>
            SameJson("contract", IdentityChecks.Guarantee(D(plans["contract"])), D(ka["guarantee"])["contract"])
            ?? SameJson("legacy", IdentityChecks.Guarantee(D(plans["legacy"])), D(ka["guarantee"])["legacy"])
            ?? SameJson("empty", IdentityChecks.Guarantee(new PyDict()), D(ka["guarantee"])["empty"]));
    }
}
