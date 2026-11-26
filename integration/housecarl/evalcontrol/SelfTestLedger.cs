using System.Text;
using RunControl;

namespace EvalControl;

/// <summary>runcontrol's ledger scripts that carry import facts (known-answers/ledger.json: moved from runcontrol's
/// finalize.json with the facts, answers unchanged), run on an overlay's ledger (ImportRows.OverlayLedger). The step
/// interpreter is runcontrol's self-test's, as there.</summary>
public static partial class SelfTest
{
    static string? K(PyDict kw, string key) => (string?)kw.Get(key);

    static string? Sha(string path) => File.Exists(path) ? Hashing.Sha256Bytes(File.ReadAllBytes(path)) : null;

    static void LedgerScriptCases(PyDict ka)
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
                        case "new": led = ImportRows.OverlayLedger(t.Root, plan); got = led.Readable; break;
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
                    byte[] got = File.ReadAllBytes(t.At(S(f["path"]))), want = RowBytes(f, t);
                    if (!got.AsSpan().SequenceEqual(want))
                        return Same(S(f["path"]), Encoding.UTF8.GetString(got), Encoding.UTF8.GetString(want)) ?? $"{S(f["path"])}: the bytes differ";
                }
                return null;
            });
    }
}
