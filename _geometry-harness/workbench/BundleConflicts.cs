using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HarnessShared;

namespace Workbench;

/// <summary>Which cell a row opens in. A reference filed in a worldspace's persistent cell (the names enrichment marks it
/// <c>!persistent</c>) is drawn in the grid cell its position falls in, never in the persistent container, so a pair opens
/// in its other reference's cell when that one has a cell of its own (capture_acquire's physical-cell rule); otherwise the
/// row has no cell to open, and says why.</summary>
internal static class CellRule
{
    internal const string PersistentWhy = "its reference is filed in its worldspace's persistent cell, which is not built as one cell";
    internal static bool Persistent(string? cell) => cell is not null && cell.EndsWith("!persistent", StringComparison.OrdinalIgnoreCase);
    internal static string? Drawable(string? cell, string? other) => !Persistent(cell) ? cell : other is not null && !Persistent(other) ? other : null;
}

/// <summary>One row of the Conflicts tab: one row identity of a placed-object check's emit, as conflictdiff keys it
/// (<see cref="ConflictIo.RowIdentity"/>: one per Necessary record), with what the bundle says about its two references.</summary>
internal sealed record ConflictRow(
    string Identity, string CheckId, string Kind, string A, string B, string Summary,
    string RefKey, string? OtherKey, PhysicalState? Physical,
    string? Cell, string? RefBaseType, string? RefBaseEditorId, string? OtherBaseType, string? OtherBaseEditorId,
    string EvidenceStrength, bool? NewOnArm);

/// <summary>A verified evaluation bundle, read for the Conflicts tab (DESIGN.md section 3). The bundle is verified by
/// the controller's own verb (<c>evalrun.py verify-bundle DIR --out FILE</c>) and every file a receipt names is read
/// from the path that verb resolved and checked (<c>receiptOutputs</c> in its verdict), never from a guessed root.
/// Anything the app cannot read that way is refused in one sentence.</summary>
/// <summary>A ticked check's state on the arm: ran (with its rows), rejected, blocked, not-applicable, and why.</summary>
internal sealed record CheckStatus(string CheckId, string State, int? Rows, string? Why);

internal sealed class BundleConflicts
{
    internal static readonly string[] PlacedChecks = { "geometry.object-overlap", "geometry.placement-contact", "geometry.fixture-empty-space" };

    internal required string Dir { get; init; }
    internal required string PlanId { get; init; }
    internal required string Arm { get; init; }
    internal required IReadOnlyList<string> Targets { get; init; }
    internal required string VerifyPath { get; init; }
    internal required string VerifySummary { get; init; }
    internal required IReadOnlyList<ConflictRow> Rows { get; init; }
    /// <summary>The Findings tab's rows: every other check's accepted receipt outputs on the arm.</summary>
    internal IReadOnlyList<FindingRow> Findings { get; init; } = Array.Empty<FindingRow>();
    /// <summary>Each ticked check on the arm: ran with its row count, or why it did not (rejected, blocked, not applicable).</summary>
    internal IReadOnlyList<CheckStatus> Checks { get; init; } = Array.Empty<CheckStatus>();

    /// <summary>The arm's candidate order file the bundle carries (its plugins and mod folders), or one sentence why not.</summary>
    internal static string CandidateFile(string bundleDir, string arm)
    {
        using var plan = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(bundleDir, "plan.json")));
        var a = plan.RootElement.GetProperty("arms").EnumerateArray().FirstOrDefault(x => x.GetProperty("armId").GetString() == arm);
        if (a.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidOperationException($"the plan has no arm '{arm}'.");
        var cand = Path.Combine(bundleDir, "candidates", a.GetProperty("candidateFile").GetString()!);
        if (!File.Exists(cand)) throw new InvalidOperationException($"the bundle does not carry arm '{arm}''s candidate file {Path.GetFileName(cand)}.");
        return cand;
    }

    /// <summary>The arm a bundle opens on: the plan's review arm (manual mode), else baseline.</summary>
    internal static string DefaultArm(string dir)
    {
        using var p = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "plan.json")));
        return p.RootElement.TryGetProperty("reviewArm", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } a ? a : "baseline";
    }
    internal required IReadOnlyList<string> Notes { get; init; }
    /// <summary>Every emit file read, bundle-relative, with the receipt that named it.</summary>
    internal required IReadOnlyList<(string Receipt, string Path)> EmitsRead { get; init; }

    static string Sha(string path) { using var fs = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant(); }

    /// <summary>Verify <paramref name="dir"/> with the controller and write its verdict to <paramref name="verifyOut"/>
    /// (deleted first, so a verdict from an earlier run is never read as this one's).</summary>
    internal static string Verify(string dir, string verifyOut, string evalrunPy, string python = "python")
    {
        if (File.Exists(verifyOut)) File.Delete(verifyOut);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(verifyOut))!);
        var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(evalrunPy); psi.ArgumentList.Add("verify-bundle"); psi.ArgumentList.Add(dir);
        psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(verifyOut);
        psi.Environment["PYTHONIOENCODING"] = "utf-8"; psi.Environment["PYTHONUTF8"] = "1";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"'{python}' could not be started to verify the bundle; put Python on PATH.");
        var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        var said = (outTask.Result + errTask.Result).Trim();
        if (!File.Exists(verifyOut))
            throw new InvalidOperationException($"evalrun verify-bundle wrote no verdict for '{dir}' (exit {p.ExitCode}): {Last(said)}");
        return Last(said);
    }

    static string Last(string s) => s.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("verify:")) ?? s.Split('\n').Last().Trim();

    /// <summary>Open a bundle: verify it, then read the arm's placed-object rows. Throws a one-sentence refusal.</summary>
    internal static BundleConflicts Open(string dir, string arm, string verifyOut, string evalrunPy, string python = "python")
    {
        dir = Path.GetFullPath(dir);
        var said = Verify(dir, verifyOut, evalrunPy, python);
        return Read(dir, arm, verifyOut, said);
    }

    /// <summary>Read the bundle against an existing verdict (what <see cref="Open"/> does after verifying).</summary>
    internal static BundleConflicts Read(string dir, string arm, string verifyPath, string verifySummary)
    {
        using var vdoc = JsonDocument.Parse(File.ReadAllText(verifyPath));
        var v = vdoc.RootElement;
        var b = v.GetProperty("bundle");
        if (!b.GetProperty("structurallyValid").GetBoolean() || !b.GetProperty("complete").GetBoolean())
        {
            string why = b.GetProperty("defects").EnumerateArray().Select(d => d.GetProperty("detail").GetString()).FirstOrDefault()
                         ?? (b.GetProperty("mismatched").GetArrayLength() > 0 ? $"{b.GetProperty("mismatched").GetArrayLength()} file(s) do not hash to what the manifest recorded"
                         : b.GetProperty("missing").GetArrayLength() > 0 ? $"{b.GetProperty("missing").GetArrayLength()} file(s) the manifest lists are missing" : "it is not structurally valid");
            throw new InvalidOperationException($"the bundle at '{dir}' does not verify ({why}); open a bundle that evalrun verify-bundle passes.");
        }
        var guarantee = v.GetProperty("toolIdentity").GetProperty("guarantee").GetString();
        if (guarantee != "verified")
            throw new InvalidOperationException($"the bundle at '{dir}' has tool identity '{guarantee}', not 'verified', so which build produced its rows cannot be checked; open a bundle made under the identity contract.");
        if (!v.TryGetProperty("receiptOutputs", out var ro))
            throw new InvalidOperationException("this evalrun's verify-bundle lists no receiptOutputs, so the receipts' files cannot be resolved; use the evalrun of this checkout.");

        // receipt key -> (recorded path -> resolved entry)
        var resolved = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (var e in ro.EnumerateArray())
        {
            var k = e.GetProperty("receipt").GetString()!;
            if (!resolved.TryGetValue(k, out var m)) resolved[k] = m = new(StringComparer.Ordinal);
            m[e.GetProperty("recordedPath").GetString()!] = e.Clone();
        }

        using var pdoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "plan.json")));
        var plan = pdoc.RootElement;
        var planId = plan.GetProperty("planId").GetString()!;
        if (!plan.GetProperty("arms").EnumerateArray().Any(a => a.GetProperty("armId").GetString() == arm))
            throw new InvalidOperationException($"the bundle's plan has no arm '{arm}'; name one of {string.Join(", ", plan.GetProperty("arms").EnumerateArray().Select(a => a.GetProperty("armId").GetString()))}.");
        var targets = plan.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
        var strength = new Dictionary<string, string>();
        foreach (var c in plan.GetProperty("checks").EnumerateArray())
            if (c.TryGetProperty("evidence", out var ev) && ev.TryGetProperty("strength", out var st) && st.ValueKind == JsonValueKind.String)
                strength[c.GetProperty("checkId").GetString()!] = st.GetString()!;

        var names = Names.Load(Path.Combine(dir, "enrichment", "names", "names.json"));
        var baselineArm = plan.GetProperty("arms").EnumerateArray().Where(a => a.TryGetProperty("kind", out var k) && k.GetString() == "baseline")
                              .Select(a => a.GetProperty("armId").GetString()!).FirstOrDefault() ?? "baseline";
        // a receipt's outputs, read only through verify-bundle's resolution and re-hashed before reading (the rule below)
        List<(string Recorded, string Full)> Outputs(string receiptKey)
        {
            var list = new List<(string, string)>();
            var rp = Path.Combine(dir, "receipts", receiptKey + ".json");
            if (!File.Exists(rp)) return list;
            using var rd = JsonDocument.Parse(File.ReadAllText(rp));
            if (!(rd.RootElement.TryGetProperty("validation", out var vv) && vv.TryGetProperty("accepted", out var ac) && ac.GetBoolean())) return list;
            foreach (var o in rd.RootElement.GetProperty("outputs").EnumerateArray())
            {
                var recorded = o.GetProperty("path").GetString()!;
                if (!recorded.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                if (!resolved.TryGetValue(receiptKey, out var byPath) || !byPath.TryGetValue(recorded, out var entry))
                    throw new InvalidOperationException($"receipt {receiptKey} names output '{recorded}', which verify-bundle did not resolve; the bundle cannot say which file that is.");
                var rel = entry.GetProperty("path").GetString()!;
                var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!entry.GetProperty("present").GetBoolean() || !entry.GetProperty("matches").GetBoolean() || entry.GetProperty("sha256").GetString() is not { } want || Sha(full) != want)
                    throw new InvalidOperationException($"receipt {receiptKey}'s output '{rel}' is missing or does not hash to the receipt; re-verify the bundle.");
                list.Add((rel, full));
            }
            return list;
        }
        string? ReceiptKey(string check, string a) => new[] { $"{check}__{a}__arm", $"{check}__{a}" }.FirstOrDefault(k => File.Exists(Path.Combine(dir, "receipts", k + ".json")));
        string? BaselineEmit(string check) => ReceiptKey(check, baselineArm) is { } k ? Outputs(k).Select(x => x.Full).FirstOrDefault() : null;
        string? ArmEmit(string k) => Outputs(k).Select(x => x.Full).FirstOrDefault();
        var notes = new List<string>();
        if (names is null) notes.Add("the bundle carries no names enrichment, so no base type is known and every row's severity reads 'unread'");
        var rows = new List<ConflictRow>();
        var seen = new HashSet<string>();
        var read = new List<(string, string)>();

        foreach (var check in PlacedChecks)
        {
            // The arm's measurement receipt of this check: <check>__<arm>__arm.json, or <check>__<arm>.json for a check
            // with no parts (empty space). Diff and reverse receipts are not rows.
            var keys = new[] { $"{check}__{arm}__arm", $"{check}__{arm}" }.Where(k => File.Exists(Path.Combine(dir, "receipts", k + ".json"))).ToList();
            if (keys.Count == 0) { notes.Add($"no {check} receipt on arm '{arm}'"); continue; }
            var key = keys[0];
            using var rdoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "receipts", key + ".json")));
            var rec = rdoc.RootElement;
            if (!(rec.TryGetProperty("validation", out var val) && val.TryGetProperty("accepted", out var acc) && acc.GetBoolean()))
            { notes.Add($"{key} was not accepted, so its rows are not shown"); continue; }
            HashSet<string>? newOnArm = arm == baselineArm ? null : NewRows(dir, check, arm, notes);
            if (newOnArm is not null && BaselineEmit(check) is { } be && ArmEmit(key) is { } ae)
            {
                newOnArm.UnionWith(ReviewCredit.CreatedIds(ConflictIo.Read(be), ConflictIo.Read(ae)));   // every created row, not the capped examples
                notes.RemoveAll(n => n.StartsWith(check + " on '" + arm + "': the differential counts"));
            }
            foreach (var o in rec.GetProperty("outputs").EnumerateArray())
            {
                var recorded = o.GetProperty("path").GetString()!;
                if (!recorded.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;   // the run's log, not an emit
                if (!resolved.TryGetValue(key, out var byPath) || !byPath.TryGetValue(recorded, out var entry))
                    throw new InvalidOperationException($"receipt {key} names output '{recorded}', which verify-bundle did not resolve; the bundle cannot say which file that is.");
                var rel = entry.GetProperty("path").GetString()!;
                var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!entry.GetProperty("present").GetBoolean() || !entry.GetProperty("matches").GetBoolean())
                    throw new InvalidOperationException($"receipt {key}'s output '{rel}' is missing or does not hash to the receipt; re-verify the bundle.");
                var want = entry.GetProperty("sha256").GetString();
                if (want is null || Sha(full) != want)
                    throw new InvalidOperationException($"'{rel}' changed after verify-bundle checked it; reopen the bundle.");
                read.Add((key, rel));
                var file = ConflictIo.Read(full);
                foreach (var c in file.Conflicts)
                {
                    string? other = c.Alternative is { Count: > 0 } alt ? alt[0].FormKey : null;
                    int? part = c.Alternative is { Count: > 0 } alt2 ? alt2[0].Triangle : null;
                    foreach (var r in c.Necessary)
                    {
                        var id = ConflictIo.RowIdentity(c.Kind, r.FormKey, other, c.A, part);
                        if (!seen.Add(id)) continue;
                        var rn = names?.Ref(r.FormKey); var on = other is null ? null : names?.Ref(other);
                        rows.Add(new ConflictRow(id, check, c.Kind, c.A, c.B, c.Summary, r.FormKey, other, r.Physical,
                            CellRule.Drawable(rn?.Cell, on?.Cell), rn?.BaseType, rn?.BaseEditorId, on?.BaseType, on?.BaseEditorId,
                            strength.GetValueOrDefault(check, "unknown"), newOnArm is null ? null : newOnArm.Contains(id)));
                    }
                }
            }
        }
        // finalize's discovery relevance, from the bundle's disposition, by check and arm
        JsonElement? relevanceDoc = null;
        var dispPath = Path.Combine(dir, "disposition.json");
        if (File.Exists(dispPath))
        {
            using var dd = JsonDocument.Parse(File.ReadAllText(dispPath));
            if (dd.RootElement.TryGetProperty("discovery", out var di) && di.TryGetProperty("relevance", out var rv)) relevanceDoc = rv.Clone();
        }
        JsonElement? Relevance(string check, string a) =>
            relevanceDoc is { } rd && rd.TryGetProperty(check, out var byArm) && byArm.TryGetProperty(a, out var one) ? one : null;
        var findings = new List<FindingRow>();
        foreach (var c in plan.GetProperty("checks").EnumerateArray())
        {
            var check = c.GetProperty("checkId").GetString()!;
            if (PlacedChecks.Contains(check)) continue;
            var key = ReceiptKey(check, arm);
            if (key is null) continue;
            bool conflictFile;
            using (var rd = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "receipts", key + ".json"))))
                conflictFile = rd.RootElement.TryGetProperty("adapter", out var ad) && ad.TryGetProperty("output", out var ot) && ot.GetString() == "conflict-file";
            var outs = Outputs(key);
            if (outs.Count == 0) { notes.Add($"{key} is not accepted or has no json output, so it gives no Findings rows"); continue; }
            var baseKey = arm == baselineArm ? null : ReceiptKey(check, baselineArm);
            var ev = strength.GetValueOrDefault(check, "unknown");
            foreach (var (rel, full) in outs)
            {
                read.Add((key, rel));
                IEnumerable<FindingRow> got;
                HashSet<string>? before = null;
                if (conflictFile)
                {
                    var file = ConflictIo.Read(full);
                    if (baseKey is not null && Outputs(baseKey).FirstOrDefault() is { Full: { } bf }) before = DifferentialRows.Of(ConflictIo.Read(bf)).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    got = DifferentialRows.Of(file).GroupBy(x => x.Id).Select(g => g.First()).Select(x =>
                    {
                        var rn = names?.Ref(x.FormKey);
                        string? why = rn is null ? "the names read did not reach this reference, so its cell is not known" : rn.Value.Cell is null ? "the bundle records no cell for this reference" : null;
                        var summary = file.Conflicts.FirstOrDefault(cf => cf.Kind == x.Kind && cf.A == x.A && cf.Necessary.Any(n => n.FormKey == x.FormKey))?.Summary ?? x.Kind;
                        return new FindingRow(check, x.Kind, x.A, x.B, summary, x.FormKey, rn?.Cell, why, "finding", x.Id, x.Physical) { BaseType = rn?.BaseType };
                    }).ToList();
                }
                else
                {
                    got = FindingReaders.Read(check, full, fk => names?.Ref(fk)?.Cell, Relevance(check, arm))
                        .Select(g => names?.Ref(g.RefKey ?? "") is { } rn2 ? g with { BaseType = rn2.BaseType } : g).ToList();
                    if (baseKey is not null && Outputs(baseKey).FirstOrDefault() is { Full: { } bj })
                        before = FindingReaders.Read(check, bj).Select(x => x.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                foreach (var f in got)
                    findings.Add(f with { Arm = arm, Source = rel, EvidenceStrength = ev, NewOnArm = before is null ? null : !before.Contains(f.Identity) });
            }
        }
        var checks = new List<CheckStatus>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(dir, "receipts"), $"*__{arm}*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (name != name.Split("__")[0] + "__" + arm && name != name.Split("__")[0] + "__" + arm + "__arm") continue;   // not a diff or reverse receipt
            using var rd = JsonDocument.Parse(File.ReadAllText(f));
            var r = rd.RootElement;
            var check = name.Split("__")[0];
            var exec = r.TryGetProperty("result", out var res) && res.TryGetProperty("execution", out var ex) && ex.ValueKind == JsonValueKind.String ? ex.GetString()! : "";
            if (exec is "skipped-user" or "not-opted-in" || checks.Any(c => c.CheckId == check)) continue;   // not ticked, or already listed
            bool accepted = r.TryGetProperty("validation", out var val) && val.TryGetProperty("accepted", out var acc) && acc.ValueKind == JsonValueKind.True;
            string why = string.Join("; ", (r.TryGetProperty("validation", out var v2) && v2.TryGetProperty("rejections", out var rj) && rj.ValueKind == JsonValueKind.Array
                ? rj.EnumerateArray().Select(x => $"{x.GetProperty("cause").GetString()}: {(x.TryGetProperty("detail", out var d) ? d.GetString() : "")}") : Enumerable.Empty<string>()));
            if (why.Length == 0 && res.ValueKind == JsonValueKind.Object)
                why = new[] { "why", "reason", "sentence", "note" }.Select(k => res.TryGetProperty(k, out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null).FirstOrDefault(x => x is { Length: > 0 }) ?? exec;
            string state = accepted && exec == "completed" ? "ran" : exec.StartsWith("rejected") || (!accepted && exec == "completed") ? "rejected"
                         : exec.StartsWith("blocked") ? "blocked" : exec.Length == 0 ? "no result" : exec;
            int count = rows.Count(x => x.CheckId == check) + findings.Count(x => x.CheckId == check);
            checks.Add(new CheckStatus(check, state, state == "ran" ? count : null, state == "ran" ? null : why));
        }
        // a reference filed in its worldspace's persistent cell has no cell of its own to build; a Findings row names one
        // reference, so it says so instead of handing the viewer a cell it cannot open
        findings = findings.Select(f => CellRule.Persistent(f.Cell) ? f with { Cell = null, WhyNot = CellRule.PersistentWhy } : f).ToList();
        return new BundleConflicts
        {
            Dir = dir, PlanId = planId, Arm = arm, Targets = targets, VerifyPath = verifyPath, VerifySummary = verifySummary,
            Rows = rows, Notes = notes, EmitsRead = read, Findings = findings, Checks = checks,
        };
    }

    /// <summary>Row identities the arm's differential lists as new defects (<c>orderEvaluation.created.newExamples</c> of
    /// the <c>__diff</c> receipt). The receipt keeps at most a few examples; when it counts more, the note says the key is partial.</summary>
    static HashSet<string>? NewRows(string dir, string check, string arm, List<string> notes)
    {
        var p = Path.Combine(dir, "receipts", $"{check}__{arm}__diff.json");
        if (!File.Exists(p)) { notes.Add($"no {check} differential on arm '{arm}': 'new on this arm' is not known for its rows"); return null; }
        using var d = JsonDocument.Parse(File.ReadAllText(p));
        if (!d.RootElement.TryGetProperty("orderEvaluation", out var oe) || !oe.TryGetProperty("created", out var cr)) { notes.Add($"{check}'s differential on '{arm}' records no created rows"); return null; }
        var set = cr.TryGetProperty("newExamples", out var ex) ? ex.EnumerateArray().Select(x => x.GetString()!.ToLowerInvariant()).ToHashSet() : new HashSet<string>();
        if (cr.TryGetProperty("newDefect", out var nd) && nd.GetInt32() > set.Count)
            notes.Add($"{check} on '{arm}': the differential counts {nd.GetInt32()} new rows and names {set.Count}; 'new on this arm' is marked only for those named");
        return set;
    }

    /// <summary>The bundle's names enrichment: per reference its winning cell and base, per base its type and EditorID.</summary>
    internal sealed class Names
    {
        // FormKey text, case-insensitively: the enrichment keeps each FormKey in the case its source spelled the plugin.
        readonly Dictionary<string, JsonElement> _refs, _bases;
        Names(JsonDocument doc)
        {
            _refs = doc.RootElement.GetProperty("refs").EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                       .ToDictionary(g => g.Key, g => g.First().Value.Clone(), StringComparer.OrdinalIgnoreCase);
            _bases = doc.RootElement.GetProperty("bases").EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }
        internal static Names? Load(string path) => File.Exists(path) ? new Names(JsonDocument.Parse(File.ReadAllText(path))) : null;

        internal (string? Cell, string? BaseType, string? BaseEditorId)? Ref(string formKey)
        {
            if (!_refs.TryGetValue(formKey, out var r)) return null;
            string? cell = null;
            // The cell the WINNING provider puts it in: carriedBy is in load order, so its last entry wins.
            if (r.TryGetProperty("carriedBy", out var cb2) && cb2.GetArrayLength() > 0 && r.TryGetProperty("cells", out var cells2)
                && cells2.TryGetProperty(cb2[cb2.GetArrayLength() - 1].GetString()!, out var cc) && cc.ValueKind == JsonValueKind.String)
                cell = cc.GetString();
            string? type = null, ed = null;
            if (r.TryGetProperty("base", out var bk) && bk.ValueKind == JsonValueKind.String && _bases.TryGetValue(bk.GetString()!, out var b))
            {
                type = b.TryGetProperty("type", out var t) ? t.GetString() : null;
                ed = b.TryGetProperty("editorId", out var e) ? e.GetString() : null;
            }
            return (cell, type, ed);
        }
    }
}
