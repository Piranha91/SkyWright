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
                var ledger = ImportRows.OverlayLedger(t.Root, plan);
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

    // The disposition and STATUS.md cases of the imported-evidence block (known-answers/finalize-imports.json: moved
    // from runcontrol's finalize.json with the block, answers unchanged): runcontrol's disposition over the recorded
    // run, then this module's block and STATUS.md section, as `evalcontrol finalize` adds them.
    static void ImportedEvidenceCases(PyDict ka)
    {
        foreach (var c in L(ka["cases"]).Select(D))
            Case($"finalize: disposition and STATUS.md of '{S(c["name"])}'", () =>
            {
                using var t = new Temp("b");
                BuildTree(t, ka["base"]);
                ApplyOps(t, c["ops"]);
                var plan = D(PyJson.LoadEvalFile(t.At("plan.json")));
                return Clocked(() =>
                {
                    var ledger = new Ledger(t.Root, plan);
                    var receipts = Receipts.ReadReceipts(t.Root);
                    var (integrity, bad) = EvidenceIntegrity.Check(t.Root, plan, ledger, receipts);
                    var disp = Disposition.Build(t.Root, plan, ledger, receipts, integrity, bad, AiList.NeverCompletingKinds);
                    ImportedEvidence.Add(disp, ledger, receipts, bad);
                    var pairs = bad.Select(kv => (object?)new List<object?> { kv.Key, kv.Value }).ToList();
                    return Same("integrity", t.Mask(PyJson.Dumps(integrity, indent: 1, ensureAscii: false)), Unpack(c["integrity"]))
                        ?? SameJson("bad keys", pairs, c["bad"])
                        ?? Same("disposition", t.Mask(PyJson.Dumps(disp, indent: 1, ensureAscii: false)), Unpack(c["disposition"]))
                        ?? Same("STATUS.md", t.Mask(StatusMd.Build(plan, disp, OverlayFinalize.Sections)), Unpack(c["status"]));
                });
            });
        // the media sentence Amend sets is the module's line among the limitations, just before the runtime line;
        // runcontrol's STATUS.md alone does not print it
        Case("finalize: an overlay's STATUS.md says its media sentence among the limitations", () =>
        {
            using var t = new Temp("b");
            BuildTree(t, ka["base"]);
            var plan = D(PyJson.LoadEvalFile(t.At("plan.json")));
            var ledger = new Ledger(t.Root, plan);
            var receipts = Receipts.ReadReceipts(t.Root);
            var disp = Clocked(() => Disposition.Build(t.Root, plan, ledger, receipts));
            PyValue.Dict(disp["unassessed"])["mediaUnassessed"] = OverlayFinalize.MediaSentence;
            var lines = StatusMd.Build(plan, disp, OverlayFinalize.Sections).Split('\n').ToList();
            int media = lines.IndexOf($"- media: {OverlayFinalize.MediaSentence}");
            if (media < 0 || !lines[media + 1].StartsWith("- runtime: ", StringComparison.Ordinal)) return $"the media line is not just before the runtime line (at {media})";
            return StatusMd.Build(plan, disp).Contains("- media: ", StringComparison.Ordinal) ? "runcontrol's STATUS.md printed the media line" : null;
        });
    }

    // The validator with this module's catalogue-like result kinds (known-answers/validate.json: moved from runcontrol's
    // evaluate.json with the discovery kind, answers unchanged; the step is runcontrol's self-test's).
    static void ValidateCases(PyDict ka)
    {
        foreach (var c in L(ka["validate"]).Select(D))
            Case($"validate: {S(c["name"])}", () =>
            {
                using var t = new Temp();
                var plan = PlanOf(t, c["plan"]);
                var v = new Validator(t.Root, plan) { MoreCatalogueKinds = AiList.Vocabulary.ResultKinds };
                int n = 0;
                foreach (var step in L(c["steps"]).Select(D))
                {
                    n++;
                    foreach (var f in L(step["files"]).Select(D))
                    {
                        string p = t.Write(S(f["path"]), PyJson.Utf8(t.Unmask(Unpack(f["text"])!)));
                        File.SetLastWriteTimeUtc(p, DateTime.UnixEpoch.AddSeconds(Py.Float(f["mtime"])));
                    }
                    var arm = L(plan["arms"]).Select(D).First(a => S(a["armId"]) == S(step["arm"]));
                    var pre = new Dictionary<string, string?>(StringComparer.Ordinal);
                    foreach (var (rel, h) in D(step["pre"])) pre[t.At(rel)] = h is "same" ? Hashing.Sha256File(t.At(rel)) : (string?)h;
                    var got = v.Validate(new PyDict { ["checkId"] = step["check"] }, arm, (string?)step["part"], D(step["adapter"]),
                        step["exit"] == null ? null : (long)PyValue.Int(step["exit"]), S(step["log"]), Strings(step["outputs"]).Select(t.At).ToList(), pre,
                        step["started"] == null ? null : Py.Float(step["started"]));
                    string? why = SameJson($"call {n}", t.Mask(got), step["want"]);
                    if (why != null) return why;
                }
                return null;
            });
        // moved from runcontrol's evaluate.json with the declared-reuse relaxation (ImportReuse), answers unchanged
        var reuse = D(L(ka["reuseIdentity"])[0]);
        Case("validate: an import declared as a reuse keeps its own source name as a recorded limit", () =>
        {
            using var t = new Temp();
            var plan = PlanOf(t, reuse["plan"]);
            return Rows(L(reuse["rows"]), row =>
            {
                var doc = D(t.Unmask(row["doc"]));
                var limits = new List<object?>();
                var rej = ImportReuse.IdentityChecks(new Validator(t.Root, plan), Validator.EmitIdentity(doc)!, D(L(plan["arms"])[0]), doc,
                    new PyDict { ["kind"] = "probe-emit", ["exe"] = "contactprobe" }, limits, row["declared"] as PyDict);
                return SameJson("rejections", t.Mask(rej), row["rejections"]) ?? SameJson("limits", t.Mask(limits), row["limits"]);
            });
        });
    }

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
