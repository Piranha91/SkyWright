using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessShared;

namespace Workbench;

/// <summary>One probe run of a re-check: what it wrote, which build made it, how long it took.</summary>
internal sealed record ProbeRun(string Label, string Emit, string BuildDigest, double Seconds, int Exit, string Log);

/// <summary>A re-check's answer for the open conflict row.</summary>
internal sealed record RecheckResult(RowVerdict Row, IReadOnlyList<RowVerdict> New, string Verdict, IReadOnlyList<string> Lines,
                                     string BuildSentence, double WallSeconds);

/// <summary>The detector re-run on the touched cell (DESIGN.md section 5, as built in stage 4). The probe that produced the
/// row runs twice on the population of the patch alone (<c>evalrun population</c>), with the same build: on the arm's order,
/// then on that order with the patch appended, the second with <c>--base</c> the first, so a vanished row carries its
/// re-measured predicate. conflictdiff compares the two; the row's outcome is the frozen rule (<see cref="RecheckRule"/>).
/// The bundle's own emit is never compared with a re-run: its population is the whole plan's, and conflictdiff refuses two
/// populations; and a re-run of another build would read a detector change as the patch's doing (review H2). The probe's
/// command comes from the bundle's own receipt (its adapter: args, population flag, common flags), not from this file.</summary>
internal static class Recheck
{
    internal static readonly Dictionary<string, string> ProbeOfCheck = new()
    {
        ["geometry.object-overlap"] = "overlapprobe", ["geometry.placement-contact"] = "contactprobe", ["geometry.fixture-empty-space"] = "contactprobe",
    };

    /// <summary>H2: which build of the probe runs. The bundle's recorded build (by digest, toolidentity's package rule) when
    /// a folder holding it is found; else the local build, for BOTH arms, said in the sentence; else refused.</summary>
    internal static (string? Folder, string Digest, string Sentence) ChooseBuild(string probe, string? bundleDigest,
        IEnumerable<string> candidates, string? localFolder, Func<string, string?> digestOf)
    {
        foreach (var c in candidates)
            if (bundleDigest is not null && Directory.Exists(c) && digestOf(c) == bundleDigest)
                return (c, bundleDigest, $"{probe}: the bundle's own build ({Short(bundleDigest)}), found at {c}.");
        if (localFolder is not null && Directory.Exists(localFolder) && digestOf(localFolder) is { } d)
            return (localFolder, d, bundleDigest is null
                ? $"{probe}: the bundle records no build for it; both arms re-run with the local build {Short(d)}."
                : $"{probe}: no folder found holds the bundle's build {Short(bundleDigest)}, so both arms re-run with the local build {Short(d)}; the bundle's own rows are not compared with it.");
        return (null, "", $"{probe}: neither the bundle's build {Short(bundleDigest)} nor a local build is on disk, so the re-check cannot run; build the probe or restore the bundle's.");
    }

    /// <summary>H2's guard: two emits are compared only when one build made both.</summary>
    internal static void SameBuildOrRefuse(ProbeRun a, ProbeRun b)
    {
        if (!string.Equals(a.BuildDigest, b.BuildDigest, StringComparison.Ordinal) || string.IsNullOrEmpty(a.BuildDigest))
            throw new InvalidOperationException($"the {a.Label} and {b.Label} runs were made by different probe builds ({Short(a.BuildDigest)} and {Short(b.BuildDigest)}); a re-check compares one build with itself, so it is refused.");
    }

    static string Short(string? d) => d is null ? "none" : (d.Contains(':') ? d.Split(':', 2)[1] : d)[..Math.Min(12, (d.Contains(':') ? d.Split(':', 2)[1] : d).Length)];

    /// <summary>Compare the two runs: conflictdiff's lines and verdict (a refusal by its gates is the re-check's refusal), then
    /// the row's outcome and every created row by the frozen rule.</summary>
    internal static (RowVerdict Row, IReadOnlyList<RowVerdict> New, string Verdict, List<string> Lines) Compare(ProbeRun unpatched, ProbeRun patched, string rowId, string conflictdiffExe)
    {
        SameBuildOrRefuse(unpatched, patched);
        var (exit, text) = Run(conflictdiffExe, new[] { unpatched.Emit, patched.Emit }, null);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (exit != 0)
            throw new InvalidOperationException($"conflictdiff refused the comparison (exit {exit}): " + (lines.FirstOrDefault(l => l.Contains("REFUSED") || l.Contains("! ")) ?? lines.LastOrDefault() ?? "no output").Trim());
        var verdict = lines.FirstOrDefault(l => l.TrimStart().StartsWith("verdict :"))?.Trim() ?? "no verdict line";
        var credited = DifferentialRows.Credit(ConflictIo.Read(unpatched.Emit), ConflictIo.Read(patched.Emit));
        return (RecheckRule.Of(credited, rowId), RecheckRule.NewRows(credited), verdict, lines);
    }

    /// <summary>The bundle receipt's adapter for the check on the arm, and the arm's candidate file.</summary>
    internal static (JsonElement Adapter, string Candidate) FromBundle(string bundleDir, string check, string arm)
    {
        var key = new[] { $"{check}__{arm}__arm", $"{check}__{arm}" }.FirstOrDefault(k => File.Exists(Path.Combine(bundleDir, "receipts", k + ".json")))
                  ?? throw new InvalidOperationException($"the bundle has no {check} receipt on arm '{arm}', so the probe's command is not known.");
        using var r = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundleDir, "receipts", key + ".json")));
        var adapter = r.RootElement.GetProperty("adapter").Clone();
        return (adapter, BundleConflicts.CandidateFile(bundleDir, arm));
    }

    internal static string? BundleDigest(string bundleDir, string probe)
    {
        using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundleDir, "plan.json")));
        return plan.RootElement.TryGetProperty("toolIdentity", out var ti) && ti.GetProperty("programs").TryGetProperty("probe:" + probe, out var p)
               && p.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
    }

    internal static string? BundleLocator(string bundleDir, string probe)
    {
        using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundleDir, "plan.json")));
        return plan.RootElement.TryGetProperty("toolIdentity", out var ti) && ti.GetProperty("programs").TryGetProperty("probe:" + probe, out var p)
               && p.TryGetProperty("locator", out var d) ? d.GetString() : null;
    }

    /// <summary>The arm's candidate rewritten for the profile the scene was built from (the probes refuse an order whose
    /// BaseProfile differs), and the same with the patch plugin appended and its mod folder at the top of the asset view.</summary>
    internal static (string Unpatched, string Patched) Orders(string candidate, string profile, string patchPlugin, string patchMod, string dir)
    {
        var c = JsonNode.Parse(File.ReadAllText(candidate))!.AsObject();
        c["BaseProfile"] = profile;
        c["Name"] = (string?)c["Name"] + " (re-check, unpatched)";
        var un = Path.Combine(dir, "order-unpatched.json");
        File.WriteAllText(un, c.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        c["Name"] = ((string?)c["Name"])!.Replace("unpatched", "with " + patchPlugin);
        c["Plugins"]!.AsArray().Add(patchPlugin);
        if (c["Mods"] is JsonArray mods) mods.Insert(0, patchMod);
        var changes = c["Changes"] as JsonArray ?? new JsonArray();
        changes.Add($"added {patchPlugin} ({patchMod}), written by the workbench");
        c["Changes"] = changes;
        var pa = Path.Combine(dir, "order-patched.json");
        File.WriteAllText(pa, c.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (un, pa);
    }

    /// <summary>The probe's arguments from the receipt's adapter: its args template with {order}, {emit}, {base}, {out},
    /// {arm} filled ("--base {base}" dropped when there is no base), the population flag, and the common flags.</summary>
    internal static List<string> Args(JsonElement adapter, string order, string emit, string? baseEmit, string outDir, string arm,
                                      string population, string instance, string profile, string game)
    {
        var args = new List<string>();
        var t = adapter.GetProperty("args").EnumerateArray().Select(x => x.GetString()!).ToList();
        for (int i = 0; i < t.Count; i++)
        {
            if (t[i] == "--base" && i + 1 < t.Count && t[i + 1] == "{base}") { if (baseEmit is not null) { args.Add("--base"); args.Add(baseEmit); } i++; continue; }
            args.Add(t[i].Replace("{order}", order).Replace("{emit}", emit).Replace("{base}", baseEmit ?? "").Replace("{out}", outDir).Replace("{arm}", arm));
        }
        if (adapter.TryGetProperty("commonFlags", out var cf) && cf.ValueKind == JsonValueKind.True)
            args.AddRange(new[] { "--instance", instance, "--profile", profile, "--game", game });
        if (adapter.TryGetProperty("populationFlag", out var pf) && pf.GetString() is { Length: > 0 } flag) { args.Add(flag); args.Add(population); }
        return args;
    }

    internal static (int Exit, string Text) Run(string exe, IEnumerable<string> args, string? logPath)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        var text = o.Result + e.Result;
        if (logPath is not null) File.WriteAllText(logPath, "$ " + exe + " " + string.Join(" ", args) + "\n" + text);
        return (p.ExitCode, text);
    }

    /// <summary>A patch folder as an order's Mods entry: its name when it sits directly in the instance's mods folder,
    /// else its full path.</summary>
    internal static string ModsEntry(string patchFolder, string instance)
    {
        var f = HousecarlCore.OutputFolderPolicy.Full(patchFolder);
        var mods = HousecarlCore.OutputFolderPolicy.Full(Path.Combine(HousecarlCore.Mo2Instance.LayoutRoot(instance), "mods"));
        return string.Equals(Path.GetDirectoryName(f), mods, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(f) : f;
    }

    /// <summary>The one folder under <paramref name="patchRoot"/> holding <paramref name="plugin"/>, by its full path; the
    /// re-check needs exactly one.</summary>
    internal static string PatchFolder(string patchRoot, string plugin)
    {
        var holders = Directory.Exists(patchRoot) ? Directory.EnumerateDirectories(patchRoot).Where(d => File.Exists(Path.Combine(d, plugin))).ToList() : new List<string>();
        if (holders.Count != 1)
            throw new InvalidOperationException($"{holders.Count} folders in '{patchRoot}' hold {plugin}, so the re-check cannot tell which one the write made; it needs exactly one.");
        return HousecarlCore.OutputFolderPolicy.Full(holders[0]);
    }

    /// <summary>toolidentity.py's package digest of a Release folder, computed by toolidentity.py itself.</summary>
    internal static string? PackageDigest(string folder, string harnessDir, string python = "python")
    {
        var (exit, text) = Run(python, new[] { "-c", "import sys; sys.path.insert(0, sys.argv[1]); import toolidentity; print(toolidentity.package(sys.argv[2])['digest'])", harnessDir, folder }, null);
        var line = text.Trim().Split('\n').LastOrDefault()?.Trim();
        return exit == 0 && line is not null && line.StartsWith("folder:") ? line : null;
    }

    /// <summary>The whole re-check, off the UI thread. <paramref name="say"/> gets one line per step.</summary>
    internal static RecheckResult RunAll(string bundleDir, string arm, ConflictRow row, string patchPlugin, string patchFolder,
        string instance, string profile, string game, string workDir, string evalrunPy, string conflictdiffExe,
        IEnumerable<string> buildDirs, string? localProbesRoot, Action<string> say)
    {
        var clock = Stopwatch.StartNew();
        Directory.CreateDirectory(workDir);
        if (!ProbeOfCheck.TryGetValue(row.CheckId, out var probe)) throw new InvalidOperationException($"a {row.CheckId} row has no probe re-run here.");
        var harness = Path.GetDirectoryName(Path.GetFullPath(evalrunPy))!;
        var (adapter, candidate) = FromBundle(bundleDir, row.CheckId, arm);

        var pluginPath = Path.Combine(patchFolder, patchPlugin);
        if (!File.Exists(pluginPath)) throw new InvalidOperationException($"the patch {patchPlugin} is not in '{patchFolder}'; write the patch first.");
        // the probes' view entry for the patch: its folder's name when it is a mod folder of the instance, else its full path (D1 a)
        var patchMod = ModsEntry(patchFolder, instance);
        var population = Path.Combine(workDir, "population.json");
        var (pe, pt) = Run("python", new[] { evalrunPy, "population", "--plugin", pluginPath, "--mod", patchMod, "--instance", instance, "--out", population }, Path.Combine(workDir, "population.log"));
        if (pe != 0) throw new InvalidOperationException("evalrun population refused the patch: " + pt.Trim());
        say(pt.Trim());

        var bundleDigest = BundleDigest(bundleDir, probe);
        var candidates = buildDirs.Select(d => Path.Combine(d, probe)).ToList();
        if (BundleLocator(bundleDir, probe) is { } loc) candidates.Add(loc);
        var local = localProbesRoot is null ? null : Path.Combine(localProbesRoot, probe, "bin", "Release", "net9.0");
        var (folder, digest, buildSentence) = ChooseBuild(probe, bundleDigest, candidates, local, f => PackageDigest(f, harness));
        say(buildSentence);
        if (folder is null) throw new InvalidOperationException(buildSentence);
        var exe = Path.Combine(folder, probe + ".exe");

        var (unOrder, paOrder) = Orders(candidate, profile, patchPlugin, patchMod, workDir);
        ProbeRun Go(string label, string order, string? baseEmit)
        {
            var emit = Path.Combine(workDir, $"{label}.json");
            var args = Args(adapter, order, emit, baseEmit, workDir, label, population, instance, profile, game);
            var sw = Stopwatch.StartNew();
            var (x, _) = Run(exe, args, Path.Combine(workDir, $"{label}.log"));
            sw.Stop();
            say(FormattableString.Invariant($"{probe} {label}: exit {x}, {sw.Elapsed.TotalSeconds:0.0} s"));
            if (x != 0 || !File.Exists(emit)) throw new InvalidOperationException($"{probe} {label} exited {x} without an emit; see {Path.Combine(workDir, label + ".log")}.");
            // The build that made it, read again after the run: a folder rebuilt mid-run is not the build chosen.
            var after = PackageDigest(folder, harness) ?? "";
            return new ProbeRun(label, emit, after, sw.Elapsed.TotalSeconds, x, Path.Combine(workDir, $"{label}.log"));
        }
        var un = Go("unpatched", unOrder, null);
        var pa = Go("patched", paOrder, un.Emit);
        var (rv, news, verdict, lines) = Compare(un, pa, row.Identity, conflictdiffExe);
        File.WriteAllLines(Path.Combine(workDir, "conflictdiff.txt"), lines);
        clock.Stop();
        return new RecheckResult(rv, news, verdict, lines, buildSentence, clock.Elapsed.TotalSeconds);
    }
}
