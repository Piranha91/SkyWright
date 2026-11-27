using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessShared;

namespace Workbench;

/// <summary>One probe run of a re-check: what it wrote, which build made it, how long it took.</summary>
internal sealed record ProbeRun(string Label, string Emit, string BuildDigest, double Seconds, int Exit, string Log);

/// <summary>A re-check's answer for the open conflict row.</summary>
internal sealed record RecheckResult(RowVerdict Row, IReadOnlyList<RowVerdict> New, string Verdict, IReadOnlyList<string> Lines,
                                     string BuildSentence, double WallSeconds, IReadOnlyList<RowVerdict>? Others = null, string? Disabled = null,
                                     string? ContactNote = null, string? Nudged = null, IReadOnlyList<RowVerdict>? ContactNew = null,
                                     RouteArmResult? Route = null, IReadOnlyList<string>? StillTouches = null, string? TouchesWhy = null);

/// <summary>The detector re-run on the touched cell (DESIGN.md section 5, as built in stage 4). The probe that produced the
/// row runs twice on the population of the patch alone (the run engine's <c>population</c>), with the same build: on the arm's order,
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

    /// <summary>H2: which build of the probe runs. The bundle's recorded build (by digest, the run engine's package rule) when
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
    internal static (RowVerdict Row, IReadOnlyList<RowVerdict> New, string Verdict, List<string> Lines) Compare(ProbeRun unpatched, ProbeRun patched, string rowId, string conflictdiffExe,
        CancellationToken ct = default)
    {
        SameBuildOrRefuse(unpatched, patched);
        var (exit, text) = Run(conflictdiffExe, new[] { unpatched.Emit, patched.Emit }, null, ct);
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

    internal static (int Exit, string Text) Run(string exe, IEnumerable<string> args, string? logPath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Run(psi, logPath, ct);
    }

    /// <summary>Run a prepared command to its end: its exit code and everything it printed.</summary>
    internal static (int Exit, string Text) Run(ProcessStartInfo psi, string? logPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var exe = psi.FileName; var args = psi.ArgumentList;
        using var p = Process.Start(psi)!;
        // Abort (a nudge's): the running probe is stopped, its process tree with it, and the step says so
        using var stop = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        ct.ThrowIfCancellationRequested();
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

    /// <summary>The whole re-check, off the UI thread. <paramref name="say"/> gets one line per step.</summary>
    internal static RecheckResult RunAll(string bundleDir, string arm, ConflictRow row, string patchPlugin, string patchFolder,
        string instance, string profile, string game, string workDir, RunEngine engine, string conflictdiffExe,
        IEnumerable<string> buildDirs, string? localProbesRoot, Action<string> say, IReadOnlyList<string>? disabled = null,
        IReadOnlyList<string>? moved = null, CancellationToken ct = default,
        IReadOnlyList<(string Key, SkyWright.Engine.Vec3 Home, SkyWright.Engine.Vec3 At)>? routeAt = null)
    {
        var clock = Stopwatch.StartNew();
        Directory.CreateDirectory(workDir);
        if (!ProbeOfCheck.TryGetValue(row.CheckId, out var probe)) throw new InvalidOperationException($"a {row.CheckId} row has no probe re-run here.");
        var (adapter, candidate) = FromBundle(bundleDir, row.CheckId, arm);

        var pluginPath = Path.Combine(patchFolder, patchPlugin);
        if (!File.Exists(pluginPath)) throw new InvalidOperationException($"the patch {patchPlugin} is not in '{patchFolder}'; write the patch first.");
        // the probes' view entry for the patch: its folder's name when it is a mod folder of the instance, else its full path (D1 a)
        var patchMod = ModsEntry(patchFolder, instance);
        var population = Path.Combine(workDir, "population.json");
        var (pe, pt) = Run(engine.Population(pluginPath, patchMod, instance, population), Path.Combine(workDir, "population.log"), ct);
        if (pe != 0) throw new InvalidOperationException($"{engine.Controller} population refused the patch: " + pt.Trim());
        say(pt.Trim());

        var (unOrder, paOrder) = Orders(candidate, profile, patchPlugin, patchMod, workDir);
        // one probe, both arms, one build (H2): its emits and the sentence naming the build
        (ProbeRun Un, ProbeRun Pa, string Build) Arms(string probe, JsonElement adapter, string prefix)
        {
            var bundleDigest = BundleDigest(bundleDir, probe);
            var candidates = buildDirs.Select(d => Path.Combine(d, probe)).ToList();
            if (BundleLocator(bundleDir, probe) is { } loc) candidates.Add(loc);
            var local = localProbesRoot is null ? null : HarnessShared.CheckoutLayout.ProgramFolder(localProbesRoot, probe);
            var (folder, digest, buildSentence) = ChooseBuild(probe, bundleDigest, candidates, local, f => engine.PackageDigest(f, workDir));
            say(buildSentence);
            if (folder is null) throw new InvalidOperationException(buildSentence);
            var exe = Path.Combine(folder, probe + ".exe");
            ProbeRun Go(string label, string order, string? baseEmit)
            {
                var emit = Path.Combine(workDir, $"{prefix}{label}.json");
                var args = Args(adapter, order, emit, baseEmit, workDir, label, population, instance, profile, game);
                var sw = Stopwatch.StartNew();
                var (x, _) = Run(exe, args, Path.Combine(workDir, $"{prefix}{label}.log"), ct);
                sw.Stop();
                say(FormattableString.Invariant($"{probe} {label}: exit {x}, {sw.Elapsed.TotalSeconds:0.0} s"));
                if (x != 0 || !File.Exists(emit)) throw new InvalidOperationException($"{probe} {label} exited {x} without an emit; see {Path.Combine(workDir, prefix + label + ".log")}.");
                // The build that made it, read again after the run: a folder rebuilt mid-run is not the build chosen.
                var after = engine.PackageDigest(folder, workDir) ?? "";
                return new ProbeRun(label, emit, after, sw.Elapsed.TotalSeconds, x, Path.Combine(workDir, $"{prefix}{label}.log"));
            }
            var u = Go("unpatched", unOrder, null);
            return (u, Go("patched", paOrder, u.Emit), buildSentence);
        }
        var (un, pa, buildSentence) = Arms(probe, adapter, "");
        var (rv, news, verdict, lines) = Compare(un, pa, row.Identity, conflictdiffExe, ct);
        File.WriteAllLines(Path.Combine(workDir, "conflictdiff.txt"), lines);
        if (disabled is null && moved is { Count: > 0 } && probe != "contactprobe")
        {
            // A nudge (Gate C) also re-runs contactprobe: whatever the move leaves in the air is a created placement-contact
            // row, kept apart (ContactNew) so the proof's word and the banner both see it.
            var (madapter, mwhy) = ContactArm(bundleDir, arm, moved);
            IReadOnlyList<RowVerdict>? cnew = null;
            if (mwhy is not null) say(mwhy);
            else
            {
                var (cu, cp, cbuild) = Arms("contactprobe", madapter!.Value, "contact-");
                var (_, _, cverdict, clines) = Compare(cu, cp, row.Identity, conflictdiffExe, ct);
                File.WriteAllLines(Path.Combine(workDir, "contact-conflictdiff.txt"), clines);
                cnew = RecheckRule.NewRows(DifferentialRows.Credit(ConflictIo.Read(cu.Emit), ConflictIo.Read(cp.Emit)));
                buildSentence += " " + cbuild;
                verdict += "; contactprobe " + cverdict;
                lines = lines.Concat(clines.Where(l => l.Contains("! REFUSED", StringComparison.Ordinal)).Select(l => "contactprobe " + l.Trim())).ToList();
            }
            // the third arm (Gate E): blockprobe --near on each nudged object, unpatched and patched
            var route = RouteArmAll(bundleDir, arm, moved, routeAt, unOrder, paOrder, instance, profile, game, workDir, engine, buildDirs, localProbesRoot, say, ct);
            if (route.Word != "not run") buildSentence += " blockprobe: one build for both arms.";
            say(FormattableString.Invariant($"route cut: {route.Word}: {route.Sentence} ({route.Seconds:0.0} s)"));
            // what the nudged object still touches, and whether each was there before the move (the proof's own rule)
            var others = moved.SelectMany(k => ClearRule.OthersOfRef(un.Emit, pa.Emit, rv.Id, k)).ToList();
            var stillHolding = moved.SelectMany(k => ClearRule.StillHolding(pa.Emit, k)).ToList();
            var held = moved.SelectMany(k => NudgeProofRule.HeldPairs(un.Emit, k)).ToHashSet(StringComparer.Ordinal);
            var touches = others.Where(o => o.Word != "resolved").Select(o => o.Id).Concat(stillHolding)
                .Select(NudgeProofRule.PairOf).Where(p => p is not null).SelectMany(p => p!.Split('|'))
                .Where(k => !moved.Contains(k, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string? touchesWhy = touches.Count == 0 ? null
                : NudgeProofRule.NotThereBefore(new TrialRecheck(new RecheckResult(rv, news, verdict, lines, buildSentence, 0), others, stillHolding, "", cnew, held));
            clock.Stop();
            return new RecheckResult(rv, news, verdict, lines, buildSentence, clock.Elapsed.TotalSeconds, null, null, mwhy, string.Join(", ", moved), cnew ?? Array.Empty<RowVerdict>(),
                                     route, touches, touchesWhy);
        }
        if (disabled is null)
        {
            clock.Stop();
            return new RecheckResult(rv, news, verdict, lines, buildSentence, clock.Elapsed.TotalSeconds);
        }
        // A disable also re-runs contactprobe (Gate B step 1): what rested on the disabled object is judged there, and
        // whatever it strands is a created row, shown as new. Every word is the frozen rule's (DisableRecheck).
        DifferentialRows.Credited? contact = null;
        string? contactNote = null;
        var (cadapterOrNull, why) = probe != "contactprobe" ? ContactArm(bundleDir, arm, disabled) : (null, null);
        if (why is not null) { contactNote = why; say(why); }
        else if (cadapterOrNull is { } cadapter)
        {
            var (cu, cp, cbuild) = Arms("contactprobe", cadapter, "contact-");
            var (_, _, cverdict, clines) = Compare(cu, cp, row.Identity, conflictdiffExe, ct);
            File.WriteAllLines(Path.Combine(workDir, "contact-conflictdiff.txt"), clines);
            contact = DifferentialRows.Credit(ConflictIo.Read(cu.Emit), ConflictIo.Read(cp.Emit));
            buildSentence += " " + cbuild;
            verdict += "; contactprobe " + cverdict;
        }
        var d = DisableRecheck.Of(DifferentialRows.Credit(ConflictIo.Read(un.Emit), ConflictIo.Read(pa.Emit)), contact, disabled, row.Identity);
        clock.Stop();
        return new RecheckResult(d.Row, d.New, verdict, lines, buildSentence, clock.Elapsed.TotalSeconds, d.Others, string.Join(", ", disabled), contactNote);
    }

    /// <summary>For each nudged object, where the load order places it and where the session shows it now (the staged or written
    /// place): the two places the route arm judges it at. An object not drawn is left out (its arm then says so).</summary>
    internal static IReadOnlyList<(string Key, SkyWright.Engine.Vec3 Home, SkyWright.Engine.Vec3 At)> RouteTargets(EditSession s, IReadOnlyList<string> keys)
        => keys.Where(k => Mutagen.Bethesda.Plugins.FormKey.TryFactory(k, out var fk) && s.IsDrawn(fk))
               .Select(k => { var fk = Mutagen.Bethesda.Plugins.FormKey.Factory(k); return (k, ViewProbe.RefPlacementEdit.BakedPose(s.RefOf(fk).Ref).Position, s.Current(fk).Position); })
               .ToList();

    /// <summary>The route-cut arm for a nudge: the bundle's receipt for that check on the arm, or, when the scan did not include
    /// it, the one sentence the words say instead.</summary>
    internal static string? RouteArmMissing(string bundleDir, string arm)
    {
        try { FromBundle(bundleDir, "geometry.route-cut", arm); return null; }
        catch (InvalidOperationException) { return RouteCutProof.NotInScan; }
    }

    static string Place(SkyWright.Engine.Vec3 v) => FormattableString.Invariant($"{v.X:R},{v.Y:R},{v.Z:R}");

    /// <summary>The third arm over every nudged object: blockprobe <c>--near</c> on the unpatched order (the object at its load-order
    /// place, also judged at the nudged place) and on the patched order (the other way round), so both runs judge the same set;
    /// one build for both; the first word that is not "held" is the arm's.</summary>
    internal static RouteArmResult RouteArmAll(string bundleDir, string arm, IReadOnlyList<string> moved,
        IReadOnlyList<(string Key, SkyWright.Engine.Vec3 Home, SkyWright.Engine.Vec3 At)>? at, string unOrder, string paOrder,
        string instance, string profile, string game, string workDir, RunEngine engine, IEnumerable<string> buildDirs, string? localProbesRoot,
        Action<string> say, CancellationToken ct)
    {
        if (RouteArmMissing(bundleDir, arm) is { } missing) return new("not run", missing, Array.Empty<string>());
        var clock = Stopwatch.StartNew();
        // The local build for both runs: the arm needs --near and the Judged list, which a scan's own (older) build may lack, and
        // it compares its two runs with each other, never with the bundle's rows, so the bundle's build is not needed (H2 holds).
        var local = localProbesRoot is null ? null : HarnessShared.CheckoutLayout.ProgramFolder(localProbesRoot, "blockprobe");
        string? digest = local is not null && Directory.Exists(local) ? engine.PackageDigest(local, workDir) : null;
        if (local is null || digest is null)
            return new("not confirmed", "walking routes could not be checked: blockprobe is not built here; build SkyWright (scripts\\build.ps1)", Array.Empty<string>(), clock.Elapsed.TotalSeconds);
        var folder = local;
        say($"blockprobe: the local build {Short(digest)} for both runs (the route arm compares its own two runs; the scan's build is not used)");
        var exe = Path.Combine(folder, "blockprobe.exe");
        var remains = new List<string>();
        var sentences = new List<string>();
        foreach (var key in moved)
        {
            ct.ThrowIfCancellationRequested();
            var t = at?.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (t is not { Key: not null } place)
                return new("not confirmed", $"walking routes could not be checked: where {key} stands was not known to the re-check", Array.Empty<string>(), clock.Elapsed.TotalSeconds);
            var origin = Mutagen.Bethesda.Plugins.FormKey.Factory(key).ModKey.FileName.String;
            var tag = "route-" + string.Concat(key.Split(':')[0].Where(char.IsLetterOrDigit));
            (int Exit, string Rows, string Log, double Seconds) Go(string label, string order, SkyWright.Engine.Vec3 also)
            {
                var rows = Path.Combine(workDir, $"{tag}-{label}.route-cut-rows.json");
                var log = Path.Combine(workDir, $"{tag}-{label}.log");
                var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in new[] { "--order", order, origin, "--near", key, "--also-at", Place(also), "--rows", rows }) psi.ArgumentList.Add(a);
                // blockprobe reads the instance, profile and game from the environment (it takes no --instance)
                psi.Environment["HOUSECARL_MO2_INSTANCE"] = instance; psi.Environment["HOUSECARL_MO2_PROFILE"] = profile; psi.Environment["HOUSECARL_GAME_PATH"] = game;
                var sw = Stopwatch.StartNew();
                var (x, _) = Run(psi, log, ct);
                say(FormattableString.Invariant($"blockprobe {label} (near {key}): exit {x}, {sw.Elapsed.TotalSeconds:0.0} s"));
                return (x, rows, log, sw.Elapsed.TotalSeconds);
            }
            var u = Go("unpatched", unOrder, place.At);
            var p = Go("patched", paOrder, place.Home);
            foreach (var (x, rows, log, _) in new[] { u, p })
                if (x != 0 || !File.Exists(rows))
                    return new("not confirmed", $"walking routes could not be checked: blockprobe exited {x} without its rows; see {log}", Array.Empty<string>(), clock.Elapsed.TotalSeconds);
            if (engine.PackageDigest(folder, workDir) is not { } after || after != digest)
                return new("not confirmed", "walking routes could not be checked: the blockprobe build changed between its two runs", Array.Empty<string>(), clock.Elapsed.TotalSeconds);
            var r = RouteCutProof.Judge(RouteCutProof.Read(u.Rows), RouteCutProof.Read(p.Rows), key);
            if (r.Word != "held") return r with { Seconds = clock.Elapsed.TotalSeconds };
            remains.AddRange(r.Remains); sentences.Add(r.Sentence);
        }
        return new("held", string.Join("; ", sentences), remains, clock.Elapsed.TotalSeconds);
    }

    /// <summary>The placement-contact arm a disable's re-check adds: the bundle's receipt for that check, or, when the scan
    /// did not include it, no adapter and the one sentence the re-check says instead.</summary>
    internal static (JsonElement? Adapter, string? Why) ContactArm(string bundleDir, string arm, IReadOnlyList<string> disabled)
    {
        try { return (FromBundle(bundleDir, "geometry.placement-contact", arm).Adapter, null); }
        catch (InvalidOperationException)
        {
            return (null, $"what rested on {string.Join(", ", disabled)} was not judged, because this scan did not include placement contact; "
                          + "scan again with the placement-contact check ticked to have it judged.");
        }
    }

    /// <summary>The banner a finished re-check says: the open row's word, and for a disable every other row naming a
    /// disabled object and, beside the word, the sentence when what rested on it was not judged.</summary>
    internal static string Banner(int rank, RecheckResult r, Func<string, string>? nameOf = null)
    {
        if (r.Nudged is not null) return NudgeBanner(rank, r, nameOf ?? (k => k));
        string what = r.Disabled is not null ? $"the disabled object(s) {r.Disabled}" : r.Nudged is not null ? $"the nudged object(s) {r.Nudged}" : "the moved reference";
        var created = r.New.Concat(r.ContactNew ?? Array.Empty<RowVerdict>()).ToList();
        string news = created.Count == 0 ? $"no new conflict involving {what}" : $"{created.Count} new conflict(s) involving {what}: " + string.Join("; ", created.Select(n => n.Id));
        string word = r.ContactNote is { } note ? $"{r.Row.Word} as an overlap, but {note}" : r.Row.Word;
        if (r.Others is { Count: > 0 } others)
            news += $". The {others.Count} other row(s) naming them: " + string.Join(", ", others.GroupBy(o => o.Word).Select(g => $"{g.Count()} {g.Key}"));
        return FormattableString.Invariant($"re-check of conflict {rank}: {word}. {news}. ({r.Verdict}; {r.WallSeconds:0.0} s)");
    }

    /// <summary>A nudge's re-check in a few words, for the Edits line (review E2); the same conditions as <see cref="NudgeBanner"/>.</summary>
    internal static string NudgeOutcome(RecheckResult r)
    {
        if (r.New.Concat(r.ContactNew ?? Array.Empty<RowVerdict>()).Any()) return "not resolved: it made a new conflict or left something in the air";
        if (r.Row.Word != "resolved") return r.Row.Word == "still there" ? "not resolved: the conflict is still there" : $"not resolved: the conflict reads {r.Row.Word}";
        if (r.Route?.Word == "cut") return "not resolved: it blocks a walking route";
        var missing = NudgeMissing(r);
        if (missing.Count == 0 && r.TouchesWhy is null) return "resolved";
        if (missing.Count == 0) return "not called resolved: it touches something not shown to have been there before the move";
        return "not called resolved: " + string.Join(", and ", missing.Select(x => System.Text.RegularExpressions.Regex.Replace(x, @" \(.*\)$", "")));
    }

    /// <summary>What a nudge's re-check did not check, in the order the banner says it.</summary>
    static List<string> NudgeMissing(RecheckResult r)
    {
        var missing = new List<string>();
        if (r.Route?.Word != "held")
            missing.Add(r.Route is null ? "walking routes were not checked" : r.Route.Word == "not run" ? $"walking routes were not checked ({r.Route.Sentence})" : $"walking routes could not be shown uncut ({r.Route.Sentence})");
        if (!(r.ContactNote is null && r.ContactNew is not null)) missing.Add($"what rests on it was not judged ({r.ContactNote ?? "placement contact did not run"})");
        return missing;
    }

    /// <summary>The banner after a nudge's commit is re-checked (Gate E): two or three plain sentences in the modder's words, with
    /// the probes' lines left to the Log. "Resolved" only when the open row resolved, nothing new was made, placement contact ran
    /// and the route arm held; otherwise it says which was not checked, or what went wrong.</summary>
    internal static string NudgeBanner(int rank, RecheckResult r, Func<string, string> nameOf)
    {
        var moved = (r.Nudged ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries);
        string me = string.Join(" and ", moved.Select(nameOf));
        var other = NudgeProofRule.PairOf(r.Row.Id)?.Split('|').FirstOrDefault(k => !moved.Contains(k, StringComparer.OrdinalIgnoreCase));
        string them = other is null ? "the other object" : nameOf(other);
        string head = $"Re-check of conflict {rank} after the commit";
        var created = r.New.Concat(r.ContactNew ?? Array.Empty<RowVerdict>()).ToList();
        if (created.Count > 0)
            return $"{head}: not resolved. The nudge of {me} made {created.Count} new conflict(s) or left something in the air; the Log names each.";
        if (r.Row.Word != "resolved")
            return $"{head}: not resolved. The conflict between {me} and {them} {(r.Row.Word == "still there" ? "is still there" : $"reads {r.Row.Word}")}; the Log has the probes' lines.";
        if (r.Route?.Word == "cut")
            return $"{head}: not resolved. {me} is clear of {them}, but {r.Route.Sentence}.";
        var touches = r.StillTouches is { Count: > 0 } st
            ? r.TouchesWhy is null ? $", and it still touches {string.Join(" and ", st.Select(nameOf))}, as it did before the move"
                                   : $", but it touches {string.Join(" and ", st.Select(nameOf))}, which is not shown to have been there before the move"
            : "";
        bool contactOk = r.ContactNote is null && r.ContactNew is not null;
        bool routeOk = r.Route?.Word == "held";
        var oldCut = r.Route is { Remains.Count: > 0 } rr ? $"; {string.Join(" and ", rr.Remains.Select(nameOf))} already blocked a walking route before the move and still {(rr.Remains.Count == 1 ? "does" : "do")}" : "";
        if (contactOk && routeOk && r.TouchesWhy is null)
            return $"{head}: resolved. {me} is clear of {them}, nothing is left in the air and no walking route is cut{touches}{oldCut}.";
        var missing = NudgeMissing(r);
        string tail = missing.Count == 0 ? "So it is not called resolved." : $"{char.ToUpperInvariant(missing[0][0])}{missing[0][1..]}{string.Concat(missing.Skip(1).Select(m => ", and " + m))}, so it is not called resolved.";
        return $"{head}: {me} is clear of {them}{(contactOk ? " and nothing is left in the air" : "")}{touches}. {tail}";
    }
}
