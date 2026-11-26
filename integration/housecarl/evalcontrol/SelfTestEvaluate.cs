using System.Text.RegularExpressions;
using RunControl;

namespace EvalControl;

/// <summary>The module's evaluate loop (OverlayEvaluate.Rows) over a recorded overlay and the app bundle it is bound
/// to, invocation by invocation (known-answers/evaluate.json: moved from runcontrol's rows.json with the import-row rule,
/// under the name it had there).</summary>
public static partial class SelfTest
{
    static readonly Regex MtimeText = new("\"mtimeUtc\": \"[^\"]+\"");

    static void EvaluateCases(PyDict ka)
    {
        foreach (var c in L(ka["execute"]).Select(D)) EvaluateCase(c);
        // runcontrol's disposition counts only its unsupported and manual checks since the import rows moved; an
        // overlay's adds its record imports and its media row (OverlayFinalize.Amend)
        Case("finalize: an overlay's families unsupported or manual add its record imports and its media row", () =>
        {
            var plan = D(PyJson.Loads("{\"checks\": ["
                + "{\"checkId\": \"records.cell-attributes\", \"family\": \"records\", \"adapter\": {\"kind\": \"mcp-only\"}}, "
                + "{\"checkId\": \"runtime.supplied-observations\", \"family\": \"runtime\", \"adapter\": {\"kind\": \"media\"}}, "
                + "{\"checkId\": \"patch.discovery-installed\", \"family\": \"patch\", \"adapter\": {\"kind\": \"patchfind\"}}, "
                + "{\"checkId\": \"runtime.state-switch-flip\", \"family\": \"state\", \"adapter\": {\"kind\": \"live-import\"}}]}"));
            var fromRuncontrol = D(PyJson.Loads("{\"familiesUnsupportedOrManual\": [\"terrain\", \"environment\"]}"));
            return SameJson("families", OverlayFinalize.Families(fromRuncontrol, plan), PyJson.Loads("[\"environment\", \"records\", \"runtime\", \"terrain\"]"))
                   ?? SameJson("with none from runcontrol", OverlayFinalize.Families(new PyDict(), plan), PyJson.Loads("[\"records\", \"runtime\"]"));
        });
    }

    static void EvaluateCase(PyDict c) => Case($"sweep and derived rows: {S(c["name"])}", () =>
    {
        using var t = new Temp("ai");
        BuildTimed(t, c["tree"]);
        int n = 1;
        foreach (var step in L(c["steps"]).Select(D))
        {
            n++;
            if (step["edits"] != null) ApplyState(t, step["edits"]);
            var plan = D(PyJson.LoadEvalFile(t.At("plan.json")));
            int advanced = 0, children = 0;
            var (_, stdout, _) = Clocked(() => Captured(() =>
            {
                var ledger = new Ledger(t.Root, plan);
                ledger.OpenInvocation("run", $"run-{n}", null);
                var ex = new Executor(t.Root, plan, ledger, resume: false) { ParentRunId = $"parent-{n}" };
                ex.RunJob = _ => throw new InvalidOperationException("a child was started where the recorded run started none");
                OverlayEvaluate.Rows(ex, t.Root, plan, ledger, () => { advanced++; });
                children = ex.Children;
                return 0;
            }));
            string Text(string rel) => MtimeText.Replace(t.Mask(Lines(t.At(rel)))!, "\"mtimeUtc\": \"<mtime>\"");
            var want = D(step["want"]);
            string? why = Same("rows counted", advanced.ToString(), Ev.Str(step["advanced"]))
                          ?? Same("children", children.ToString(), Ev.Str(step["children"]))
                          ?? Same("printed", t.Mask(stdout), S(step["stdout"]))
                          ?? Same("ledger.json", Text("ledger.json"), Unpack(want["ledger"]))
                          ?? Same("journal.jsonl", Text("journal.jsonl"), Unpack(want["journal"]));
            if (why == null)
            {
                var keys = Directory.GetFiles(t.At("receipts"), "*.json").Select(p => Path.GetFileNameWithoutExtension(p)).ToList();
                why = Same("receipts written", string.Join(" ", PyOrder.SortedByCodePoint(keys)), string.Join(" ", PyOrder.SortedByCodePoint(D(want["receipts"]).Keys)));
                foreach (var (key, text) in D(want["receipts"]))
                    why ??= Same($"receipt {key}", Text($"receipts/{key}.json"), Unpack(text));
                var files = Directory.GetFiles(t.Root, "*", SearchOption.AllDirectories).Select(p => PyOrder.Fwd(Path.GetRelativePath(t.Root, p)))
                    .Where(rel => !rel.StartsWith("receipts/", StringComparison.Ordinal));
                why ??= Same("files", string.Join(" ", PyOrder.SortedByCodePoint(files)), string.Join(" ", Strings(want["files"])));
            }
            if (why != null) return $"step '{S(step["name"])}': {why}";
        }
        return null;
    });

    // The folders brought to a recorded state: files under the overlay it does not list removed, files whose bytes
    // differ written (the app bundle beside it is written the same way).
    static void ApplyState(Temp t, object? tree)
    {
        var rows = L(tree).Select(D).ToList();
        var listed = rows.Select(r => S(r["path"])).ToHashSet(StringComparer.Ordinal);
        foreach (string p in Directory.GetFiles(t.Root, "*", SearchOption.AllDirectories))
            if (!listed.Contains(PyOrder.Fwd(Path.GetRelativePath(t.Root, p)))) File.Delete(p);
        foreach (var row in rows.Where(r => !r.ContainsKey("planSha")))
        {
            byte[] want = RowBytes(row, t);
            string p = t.At(S(row["path"]));
            if (!File.Exists(p) || !File.ReadAllBytes(p).AsSpan().SequenceEqual(want)) t.Write(S(row["path"]), want);
        }
    }
}
