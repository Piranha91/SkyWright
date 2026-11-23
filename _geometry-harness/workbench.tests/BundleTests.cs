using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessShared;
using Workbench;
using Xunit;

namespace WorkbenchTests;

/// <summary>A real schema-2 bundle made by the controller itself (tests/fixtures/evalreport/make_report_fixtures.py, case
/// R3: plan, run, finalize, bundle over the fixture engine), generated once per test run into a temp folder. Like the
/// real bundles, it holds each sweep emit twice: the receipt's evidence copy under <c>evidence/</c> and the sweep copy at
/// the bundle root under the same relative path.</summary>
public sealed class R3Bundle : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc-wb-bundle-" + Guid.NewGuid().ToString("N"));
    public string Bundle { get; }
    public static string Checkout()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, HarnessShared.CheckoutLayout.Marker))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
    public static string Runcontrol => Environment.GetEnvironmentVariable("WB_RUNCONTROL") is { Length: > 0 } e ? e
        : TestBuild.Runcontrol;

    public R3Bundle()
    {
        var gen = Path.Combine(Checkout(), "_geometry-harness", "tests", "fixtures", "evalreport", "make_report_fixtures.py");
        var psi = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(gen); psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(Root); psi.ArgumentList.Add("--only"); psi.ArgumentList.Add("R3");
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"the fixture generator failed ({p.ExitCode}): {o.Result}{e.Result}");
        using var idx = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "index.json")));
        var b = idx.RootElement.GetProperty("R3-many-findings-long-names").GetProperty("bundle").GetString()!;
        Bundle = Path.IsPathRooted(b) ? b : Path.GetFullPath(Path.Combine(Root, "..", b));
        if (!Directory.Exists(Bundle)) Bundle = Path.Combine(Root, "runs", "R3-many-findings-long-names", "bundle");
        Assert.True(File.Exists(Path.Combine(Bundle, "manifest.json")), $"no bundle at {Bundle}");
    }

    /// <summary>A fresh copy of the bundle to damage.</summary>
    public string Copy()
    {
        var dst = Path.Combine(Root, "copy-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var f in Directory.EnumerateFiles(Bundle, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(dst, Path.GetRelativePath(Bundle, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t);
        }
        return dst;
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

/// <summary>Stage-3 review H1: every file a receipt names is read from the path the controller's verify-bundle resolved
/// and checked, never from a second guess, and a bundle that does not verify is refused.</summary>
[Trait("tier", "integration")]
public class BundleTests : IClassFixture<R3Bundle>
{
    const string Overlap = "sweep/inv-0001/overlapprobe/baseline.json";
    readonly R3Bundle _b;
    public BundleTests(R3Bundle b) => _b = b;

    static string Sha(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();

    /// <summary>Put a different file of the same name at the bundle root (the wrong root for a receipt's output) and
    /// record its hash in the manifest, so the bundle still verifies.</summary>
    static void PlantDecoy(string dir)
    {
        var decoy = new ConflictFile("overlapprobe", "decoy", "2026-11-14T00:00:00Z", new[]
        {
            new Conflict("OVERLAP", "Decoy.esp", "DecoyToo.esp", "a decoy row at the wrong root",
                new[] { new ConflictRecord("000D0C:Decoy.esp", "placed-ref", null, PhysicalState.Holds) },
                new[] { new ConflictRecord("000D0D:Decoy.esp", "placed-ref", null) }),
        }, Array.Empty<CoverageLine>());
        var path = Path.Combine(dir, Overlap.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), "the fixture has no sweep copy at the bundle root to replace");
        ConflictIo.Write(path, decoy);
        var mpath = Path.Combine(dir, "manifest.json");
        var m = JsonNode.Parse(File.ReadAllText(mpath))!;
        int n = 0;
        foreach (var row in m["evidence"]!.AsArray())
            if ((string?)row!["path"] == Overlap) { row["sha256"] = Sha(path); if (row["bytes"] is not null) row["bytes"] = new FileInfo(path).Length; n++; }
        Assert.Equal(1, n);
        File.WriteAllText(mpath, m.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void Rows_come_from_the_receipts_evidence_copy_and_a_same_named_decoy_at_the_bundle_root_is_not_read()
    {
        var dir = _b.Copy();
        PlantDecoy(dir);
        var work = Path.Combine(dir + "-work");
        BundleConflicts? got = null;
        var ex = Record.Exception(() => got = BundleConflicts.Open(dir, "baseline", Path.Combine(work, "verify.json"), RunEngine.OfBuiltIn(R3Bundle.Runcontrol)));
        Assert.True(ex is null, "the bundle with a decoy at the wrong root was refused: " + ex?.Message);
        Assert.DoesNotContain(got!.Rows, r => r.A == "Decoy.esp" || r.B == "Decoy.esp");
        var emit = got.EmitsRead.Single(e => e.Receipt == "geometry.object-overlap__baseline__arm");
        Assert.Equal("evidence/" + Overlap, emit.Path);
        // Every row of the evidence copy, as conflictdiff keys rows, and nothing else from that check.
        var real = ConflictIo.Read(Path.Combine(dir, "evidence", Overlap.Replace('/', Path.DirectorySeparatorChar)));
        var want = real.Conflicts.SelectMany(c => c.Necessary.Select(r => ConflictIo.RowIdentity(c.Kind, r.FormKey,
            c.Alternative is { Count: > 0 } a ? a[0].FormKey : null, c.A, c.Alternative is { Count: > 0 } a2 ? a2[0].Triangle : null))).Distinct().OrderBy(x => x).ToList();
        var have = got.Rows.Where(r => r.CheckId == "geometry.object-overlap").Select(r => r.Identity).OrderBy(x => x).ToList();
        Assert.Equal(want, have);
    }

    [Fact]
    public void A_bundle_with_one_byte_changed_in_an_emit_is_refused_in_one_sentence()
    {
        var dir = _b.Copy();
        var p = Path.Combine(dir, "evidence", Overlap.Replace('/', Path.DirectorySeparatorChar));
        var bytes = File.ReadAllBytes(p);
        int i = Array.IndexOf(bytes, (byte)'O');            // the first letter of an "OVERLAP" kind: still valid JSON
        Assert.True(i >= 0);
        bytes[i] = (byte)'Q';
        File.WriteAllBytes(p, bytes);
        var ex = Assert.ThrowsAny<Exception>(() => BundleConflicts.Open(dir, "baseline", Path.Combine(dir + "-work", "verify.json"), RunEngine.OfBuiltIn(R3Bundle.Runcontrol)));
        Assert.Contains("does not verify", ex.Message);
        Assert.DoesNotContain('\n', ex.Message.Trim());
    }

    [Fact]
    public void A_receipt_output_the_verdict_does_not_resolve_is_refused_not_guessed()
    {
        var dir = _b.Copy();
        var v = Path.Combine(dir + "-work", "verify.json");
        var said = BundleConflicts.Verify(dir, v, RunEngine.OfBuiltIn(R3Bundle.Runcontrol));
        var doc = JsonNode.Parse(File.ReadAllText(v))!;
        var outs = doc["receiptOutputs"]!.AsArray();
        var drop = outs.Where(o => (string?)o!["receipt"] == "geometry.object-overlap__baseline__arm").ToList();
        Assert.NotEmpty(drop);
        foreach (var o in drop) outs.Remove(o);
        File.WriteAllText(v, doc.ToJsonString());
        var ex = Assert.ThrowsAny<Exception>(() => BundleConflicts.Read(dir, "baseline", v, said));
        Assert.Contains("did not resolve", ex.Message);
    }

    /// <summary>Stage-3 review L-1: ranking key 4 on a candidate arm. The arm's overlap differential is made to name one
    /// row as new; that row must read "new on this arm" and rank above every row that ties with it on keys 1-3, including
    /// the ones its identity sorts after. The receipt is bound into the ledger, so an edited receipt cannot verify: the
    /// bundle is verified first and the edit made after, so this tests the reader and the ranking, not verify-bundle.</summary>
    [Fact]
    public void On_a_candidate_arm_a_row_the_differential_names_new_ranks_above_its_equals_on_keys_one_to_three()
    {
        const string Arm = "candidate-01-reorder";
        const string Diff = "receipts/geometry.object-overlap__" + Arm + "__diff.json";
        var dir = _b.Copy();
        var sevPath = Path.Combine(dir + "-work", "severity.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sevPath)!);
        File.WriteAllText(sevPath, """{ "unread": 25, "default": 25, "weights": { "*": { } } }""");
        var sev = SeverityTable.Load(sevPath);

        var v = Path.Combine(dir + "-work", "verify.json");
        var said = BundleConflicts.Verify(dir, v, RunEngine.OfBuiltIn(R3Bundle.Runcontrol));
        var first = BundleConflicts.Read(dir, Arm, v, said);
        var r0 = ConflictRanking.Rank(first.Rows, first.Targets, sev).Where(r => r.Row.CheckId == "geometry.object-overlap").ToList();
        Assert.All(r0, r => Assert.Equal("not new", r.NewLabel));
        // The largest group that ties on keys 1-3; its last row by identity is the one made new.
        var group = r0.GroupBy(r => (r.InvolvesTarget, r.PhysicalGrade, r.EvidenceGrade)).OrderByDescending(g => g.Count()).First().ToList();
        Assert.True(group.Count >= 2, "the fixture has no two overlap rows that tie on keys 1-3");
        var chosen = group.OrderBy(r => r.Row.Identity, StringComparer.Ordinal).Last().Row.Identity;

        var p = Path.Combine(dir, Diff.Replace('/', Path.DirectorySeparatorChar));
        var d = JsonNode.Parse(File.ReadAllText(p))!;
        var created = d["orderEvaluation"]!["created"]!;
        created["created"] = 1; created["newDefect"] = 1;
        created["newExamples"] = new JsonArray(chosen);
        File.WriteAllText(p, d.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var second = BundleConflicts.Read(dir, Arm, v, said);
        var r1 = ConflictRanking.Rank(second.Rows, second.Targets, sev).Where(r => r.Row.CheckId == "geometry.object-overlap").ToList();
        var hit = r1.Single(r => r.Row.Identity == chosen);
        Assert.Equal("new on this arm", hit.NewLabel);
        var ties = r1.Where(r => r.Row.Identity != chosen && (r.InvolvesTarget, r.PhysicalGrade, r.EvidenceGrade) == (hit.InvolvesTarget, hit.PhysicalGrade, hit.EvidenceGrade)).ToList();
        Assert.NotEmpty(ties);
        Assert.All(ties, t => Assert.True(hit.Rank < t.Rank, $"the new row ranks {hit.Rank}, after {t.Row.Identity} at {t.Rank}"));
        Assert.All(ties, t => Assert.Equal("not new", t.NewLabel));
    }
}
