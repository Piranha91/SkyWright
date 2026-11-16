using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HousecarlCore.Progress;

namespace Workbench;

/// <summary>A check id in words: "loadability.masters-and-order" reads "Loadability: Masters and Order".</summary>
internal static class ProbeNames
{
    static readonly HashSet<string> Small = new(StringComparer.Ordinal) { "and", "or", "of", "the", "to", "in", "on", "for", "a", "an", "by", "at" };
    static readonly Dictionary<string, string> Upper = new(StringComparer.OrdinalIgnoreCase) { ["bos"] = "BOS", ["npc"] = "NPC", ["lod"] = "LOD", ["mcp"] = "MCP", ["skse"] = "SKSE", ["fomod"] = "FOMOD" };

    internal static string Family(string id) => Word(id.Split('.')[0], first: true);

    /// <summary>Why a check of this registry kind is not a probe the launcher runs, in plain words.</summary>
    internal static string NotAProbe(string kind) => kind switch
    {
        "mcp-only" => "is checked by Claude reading the records directly",
        "derived" => "is worked out from the other checks' results",
        "manual" => "is a step done by hand",
        "media" => "is read from screenshots and video you supply",
        "live-import" => "needs the game running",
        _ => "is not built yet",
    };

    /// <summary>What a probe looks for, in game terms, for the Start tab's tiles; empty when none is written.</summary>
    internal static string Says(string id) => id switch
    {
        "loadability.masters-and-order" => "Every master is present and the load order is one the game will load.",
        "geometry.navmesh-ownership" => "Navmesh edits a later plugin throws away, and broken links in the navmesh that wins.",
        "geometry.object-overlap" => "Objects one mod placed or moved into another mod's objects.",
        "geometry.placement-contact" => "Objects left floating, or sunk, after a later plugin moved what they sat on.",
        "geometry.fixture-empty-space" => "Fixtures left hanging in empty air after another mod removed the room around them.",
        "geometry.declared-dependencies" => "Objects tied to another object (enable parent, linked reference, activate parent) that a later plugin moved apart.",
        "geometry.bos-overlay" => "Which objects the game really shows once Base Object Swapper's _SWAP.ini files apply.",
        "geometry.route-cut" => "Objects placed across a path NPCs walk.",
        "functional.quest-alias-split" => "Quest characters and objects meant to be together that the winning load order puts apart.",
        "functional.linked-dependencies" => "Where the mod leads outside its own cells: door destinations, neighbouring cells and links out.",
        "geometry.terrain-support" => "Objects left floating or buried after another mod reshaped the ground under them.",
        "state.switch-census" => "Enable markers that switch groups of objects on and off: what they switch and what flips them.",
        "state.conditional-geometry" => "Overlaps and gaps that appear only once an enable marker switches its objects on.",
        "environment.water-boundaries" => "Neighbouring cells whose water heights or types disagree.",
        "environment.land-seams" => "Neighbouring cells whose landscape edges no longer meet.",
        "patch.discovery-installed" => "Patches already installed in this instance that cover these mods.",
        "patch.discovery-fomod" => "Installer (FOMOD) options that fit this load order but were not chosen.",
        "patch.discovery-pool" => "Downloaded mods, not installed, that would patch these mods.",
        "patch.author-advice" => "What the mod author's Nexus page says about needed patches and load order.",
        _ => "",
    };

    internal static string Of(string id)
    {
        var dot = id.IndexOf('.');
        if (dot < 0) return Words(id);
        return $"{Family(id)}: {Words(id[(dot + 1)..])}";
    }

    static string Words(string s)
    {
        var parts = s.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select((p, i) => Word(p, i == 0)));
    }

    static string Word(string w, bool first)
    {
        if (Upper.TryGetValue(w, out var u)) return u;
        if (!first && Small.Contains(w)) return w;
        return w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..];
    }
}

/// <summary>One probe's state in the current run, as its own run records report it.</summary>
internal sealed record ProbeProgress(string State, double? Fraction, string Line, string? Eta);

/// <summary>Where the pipeline is, from its own run record: the stage going now (null before the first), its step, the
/// step count, when that stage started, and when the probes' own checks began (null while the run still prepares).</summary>
internal sealed record PipelineAt(string? Stage, int Step, int Steps, DateTimeOffset? StageStarted, DateTimeOffset Started, DateTimeOffset? ChecksStarted)
{
    /// <summary>The stages before the checks: the run is preparing while one of these is going.</summary>
    internal static readonly string[] Preparation = { "census", "prepare", "scope", "plan" };

    internal static string Words(string? stage) => stage switch
    {
        "census" => "reading what the selected mods change",
        "prepare" => "setting up the load orders to compare",
        "scope" => "checking that each load order loads",
        "plan" => "planning the checks",
        "evaluate" => "running the probes",
        "records" or "scenes" => "gathering what the probes found",
        "bundle" => "collecting the results",
        null => "starting",
        _ => stage,
    };

    internal string Line(DateTimeOffset now)
    {
        string prep = ChecksStarted is null ? "preparing the run: " : "";
        string since = StageStarted is { } t ? $", {Clock((now - t).TotalSeconds)}" : "";
        return $"{prep}{Words(Stage)} (step {Step} of {Steps}){since}";
    }

    static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds))).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
}

/// <summary>Probe runs read from a pipeline run's own run-log store (<c>HOUSECARL_RUNLOG_DIR</c>): each probe opens a run
/// record there with its pid, its stage counters and a remaining estimate (the run-events contract). The workbench reads
/// those records; it never invents a fraction, and a probe that reports no counters is said to report none.</summary>
internal static class ProbeRuns
{
    static IEnumerable<RunRecord> Read(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            RunRecord? r = null;
            try { r = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(f), RunContract.Json); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }   // a snapshot being replaced is read next tick
            if (r is not null) yield return r;
        }
    }

    /// <summary>The live snapshots of runs of <paramref name="op"/> in this store, newest first.</summary>
    internal static List<RunRecord> Active(string store, string op)
        => Read(Path.Combine(store, "active")).Where(r => string.Equals(r.Op, op, StringComparison.OrdinalIgnoreCase) && r.EndedUtc is null)
                                              .OrderByDescending(r => r.StartedUtc).ToList();

    /// <summary>The finished runs of <paramref name="op"/> in this store's history.</summary>
    internal static List<RunRecord> Finished(string store, string op) => History(Path.Combine(store, "history.jsonl"))
        .Where(r => string.Equals(r.Op, op, StringComparison.OrdinalIgnoreCase)).ToList();

    internal static IEnumerable<RunRecord> History(string path)
    {
        if (!File.Exists(path)) yield break;
        List<string> lines;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            lines = new List<string>();
            while (sr.ReadLine() is { } l) lines.Add(l);
        }
        catch (IOException) { yield break; }
        foreach (var l in lines)
        {
            RunRecord? r = null;
            try { r = JsonSerializer.Deserialize<RunRecord>(l, RunContract.Json); } catch (JsonException) { }
            if (r is not null) yield return r;
        }
    }

    /// <summary>Whether a run's own process is still running (its pid, checked against its recorded start time).</summary>
    static bool Alive(RunRecord r)
    {
        if (r.Pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(r.Pid);
            return !p.HasExited && (r.ProcessStartUtc is not { } s || Math.Abs((p.StartTime.ToUniversalTime() - s.UtcDateTime).TotalSeconds) <= 2);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds))).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>The pipeline's place in this store's run, or null when no pipeline record is live.</summary>
    internal static PipelineAt? Pipeline(string store)
    {
        var p = Active(store, "evalpipeline").FirstOrDefault();
        if (p is null) return null;
        string? stage = null; DateTimeOffset? at = null;
        var last = p.Notes.LastOrDefault(n => n.StartsWith("stage ", StringComparison.Ordinal));
        if (last is not null)
        {
            var parts = last.Split(' ');
            stage = parts.Length > 1 ? parts[1] : null;
            if (parts.Length > 3 && DateTimeOffset.TryParse(parts[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) at = t;
        }
        var st = p.Stages.FirstOrDefault(s => s.Id == "stages");
        int steps = (int)(st?.Total ?? 0), step = (int)Math.Min((st?.Processed ?? 0) + 1, Math.Max(1, steps));
        return new PipelineAt(stage, step, steps, at, p.StartedUtc, ChecksStart(store));
    }

    /// <summary>When the probes' own checks began in this store: the pipeline's evaluate step (its runner's start); null before.
    /// A probe run before it (the census, the load-order checks, the plan's preflight) is preparation, not a check.</summary>
    internal static DateTimeOffset? ChecksStart(string store) => ChecksStart(Read(Path.Combine(store, "active")).Concat(History(Path.Combine(store, "history.jsonl"))));

    static DateTimeOffset? ChecksStart(IEnumerable<RunRecord> records)
    {
        var starts = records.Where(r => r.Op.StartsWith("evalpipeline-evaluate", StringComparison.OrdinalIgnoreCase)).Select(r => r.StartedUtc).ToList();
        return starts.Count == 0 ? null : starts.Min();
    }

    /// <summary>How long earlier runs in these stores took to prepare (from the pipeline's start to its evaluate step), by
    /// their median, and how many runs that is; null when none is recorded.</summary>
    internal static (double Seconds, int Runs)? PreparationGuess(IEnumerable<string> historyFiles)
    {
        var took = new List<double>();
        foreach (var f in historyFiles)
        {
            var all = History(f).ToList();
            var pipe = all.Where(r => r.Op == "evalpipeline").OrderBy(r => r.StartedUtc).FirstOrDefault();
            if (pipe is null || ChecksStart(all) is not { } c) continue;
            took.Add((c - pipe.StartedUtc).TotalSeconds);
        }
        if (took.Count == 0) return null;
        took.Sort();
        return (took[took.Count / 2], took.Count);
    }

    /// <summary>What the tile shows for a probe: waiting, running with its stage counters and ETA (or "no progress reported"
    /// and the elapsed time), done, failed or aborted.</summary>
    internal static ProbeProgress Of(string? store, string op, bool aborted, bool runEnded, DateTimeOffset now, RunEstimate? first, bool pipelined = false, Func<string, string>? nameOf = null)
    {
        if (store is null) return new("idle", null, "", null);
        // in a pipeline run only the probe's runs from the checks on are this check's; earlier ones were the preparation
        DateTimeOffset? since = pipelined ? ChecksStart(store) : null;
        var pipe = pipelined ? Pipeline(store) : null;
        if (pipelined && since is null && !runEnded && !aborted)
            return new("waiting", null, pipe is null ? "waiting for the run to start" : pipe.Line(now), null);
        bool Ours(RunRecord r) => since is null || r.StartedUtc >= since.Value;
        var live = Active(store, op).Where(Ours).FirstOrDefault();
        var done = Finished(store, op).Where(Ours).ToList();
        string passes = done.Count == 0 ? "" : $"; {Runs(done.Count)} finished";
        if (aborted) return new("aborted", null, (live is null || !Alive(live) ? "aborted" : "aborting…") + passes, null);
        if (live is not null)
        {
            double elapsed = (now - live.StartedUtc).TotalSeconds;
            var stage = live.Stages.LastOrDefault(s => s.Id == live.Stage) ?? live.Stages.LastOrDefault();
            if (stage is { Total: > 0 } st)
            {
                double frac = Math.Clamp((double)st.Processed / st.Total.Value, 0, 1);
                var rem = live.Remaining is { HasNumber: true } r ? r : Estimator.Remaining(live, live.ActiveSeconds > 0 ? live.ActiveSeconds : elapsed, now);
                string eta = rem.HasNumber ? $"about {Clock(rem.Seconds!.Value)} left ({Basis(rem.Basis)})" : "no time estimate yet";
                return new("running", frac, $"{st.Title ?? st.Id} {st.Processed}/{st.Total} {st.Unit}; {Clock(elapsed)} elapsed{passes}", eta);
            }
            return new("running", null, $"{stage?.Title ?? stage?.Id ?? "running"}: this probe does not report how far it is; {Clock(elapsed)} elapsed{passes}", null);
        }
        // between runs: a probe runs once per mod setup compared, so finished runs are not the end until the whole run ends
        if (done.Count > 0 && !runEnded)
            return new("waiting", null, $"{Runs(done.Count)} finished, {Clock(done.Sum(r => r.WallSeconds))}; it runs again for each mod setup compared", null);
        if (done.Count > 0)
        {
            // the reason is the failed run's own, never the last run's when that one succeeded
            var bad = done.Where(r => r.Outcome != RunOutcome.Ok).OrderBy(r => r.StartedUtc).ToList();
            string time = $"{Runs(done.Count)}, {Clock(done.Sum(r => r.WallSeconds))}";
            return bad.Count == 0 ? new("done", 1, $"done: {time}", null)
                : new("failed", 1, $"failed: {bad.Count} of {Runs(done.Count)} did not finish ({bad[^1].Error ?? bad[^1].Outcome.ToString()}); {Clock(done.Sum(r => r.WallSeconds))}", null);
        }
        if (runEnded) return new("not run", null, "did not run this time", null);
        // "waiting its turn" only when another probe really is running ahead of it
        var ahead = since is null ? null : Read(Path.Combine(store, "active"))
            .Where(r => r.EndedUtc is null && r.StartedUtc >= since.Value && !string.Equals(r.Op, op, StringComparison.OrdinalIgnoreCase) && r.Pid > 0 && r.ParentRunId is null && r.Mode != "mcp" && !r.Op.StartsWith("evalrun", StringComparison.OrdinalIgnoreCase) && !r.Op.StartsWith("evalpipeline", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.StartedUtc).FirstOrDefault();
        string guess = first is { HasNumber: true } f ? $"; about {Clock(f.Seconds!.Value)} a run once it starts, judged from {Runs(f.Samples)} before" : "";
        if (!pipelined) return new("waiting", null, "waiting its turn" + guess, null);
        return new("waiting", null, (ahead is not null ? $"waiting its turn: {nameOf?.Invoke(ahead.Op) ?? ahead.Op} is running" : pipe is not null ? pipe.Line(now) : "waiting for the run to reach it") + guess, null);
    }

    static string Runs(int n) => n == 1 ? "1 run" : $"{n} runs";

    /// <summary>How an estimate was made, in words.</summary>
    static string Basis(string basis) => basis switch
    {
        "measured-rate" => "from its speed so far",
        "median" or "fitted" => "from earlier runs",
        "provisional" => "a rough guess from few earlier runs",
        "reference" => "from one earlier run",
        _ => "a rough guess",
    };

    /// <summary>What the run's ledger says about one check once the run has ended, or null when it has no row for it.
    /// The ledger is the run's verdict: a probe's raw exit is not (scopeprobe exits 1 on a setup that does not load the
    /// selected plugins, which the ledger accepts as that setup's answer and states as not measured there).</summary>
    internal static ProbeProgress? Verdict(string evaluationDir, string checkId)
    {
        var path = Path.Combine(evaluationDir, "ledger.json");
        if (!File.Exists(path)) return null;
        JsonElement rows;
        try { rows = JsonDocument.Parse(File.ReadAllText(path)).RootElement; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
        if (rows.TryGetProperty("entries", out var e)) rows = e;
        if (rows.ValueKind != JsonValueKind.Object) return null;
        static string? S(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var parts = new List<string>(); string? failed = null;
        foreach (var row in rows.EnumerateObject().Select(p => p.Value).Where(v => v.ValueKind == JsonValueKind.Object && S(v, "checkId") == checkId)
                                .OrderBy(v => S(v, "armId") == "baseline" ? 0 : 1))
        {
            var arm = S(row, "armId") == "baseline" ? "without the selected mods" : "with the selected mods";
            var status = S(row, "status") ?? "";
            if (status is not ("completed" or "reused"))
            {
                failed ??= $"failed {arm}: {status}" + (S(row, "cause") is { } c ? $" ({c})" : "") + (S(row, "detail") is { } d ? $": {d}" : "");
                continue;
            }
            bool notLoaded = false;
            if (S(row, "receiptPath") is { } rp && File.Exists(Path.Combine(evaluationDir, rp)))
            {
                try
                {
                    var res = JsonDocument.Parse(File.ReadAllText(Path.Combine(evaluationDir, rp))).RootElement;
                    notLoaded = res.TryGetProperty("result", out var r) && r.TryGetProperty("coverage", out var cov) && cov.ValueKind == JsonValueKind.Array
                                && cov.EnumerateArray().Any(l => l.TryGetProperty("Unjudgeable", out var u) && u.ValueKind == JsonValueKind.Object && u.TryGetProperty("not-loaded-by-this-arm", out _));
                }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }
            var said = S(row, "detector") switch
            {
                "finding" => "found something",
                "no-finding" => notLoaded ? "loads; the selected plugins are not in it, so they were not checked there" : "nothing found",
                "inconclusive" => "not settled",
                "catalogue" => "listed",
                _ => "done",
            };
            parts.Add($"{arm}: {said}");
        }
        if (failed is not null) return new("failed", 1, failed, null);
        return parts.Count == 0 ? null : new("done", 1, "done. " + string.Join("; ", parts) + ".", null);
    }

    /// <summary>The first guess for one pass of a probe, by the run-log estimator over earlier runs' histories.</summary>
    internal static RunEstimate First(IEnumerable<string> historyFiles, string op, DateTimeOffset now)
    {
        // a check's own runs only: a run of the same probe during preparation (the plan's preflight) is far shorter
        var all = new List<RunRecord>();
        foreach (var f in historyFiles)
        {
            var recs = History(f).ToList();
            var since = ChecksStart(recs);
            all.AddRange(recs.Where(r => string.Equals(r.Op, op, StringComparison.OrdinalIgnoreCase) && (since is null || r.StartedUtc >= since.Value)));
        }
        if (all.Count == 0) return RunEstimate.None("learning", $"no earlier run of {op} is recorded", now);
        var template = all.OrderBy(r => r.StartedUtc).Last();
        return Estimator.Predict(all, template, now);
    }

    /// <summary>Stop the live run(s) of <paramref name="op"/> in this store and nothing else: each run's own process, checked
    /// against its recorded start time so a reused pid is never killed. Returns the pids stopped.</summary>
    internal static List<int> Abort(string store, string op)
    {
        var stopped = new List<int>();
        foreach (var r in Active(store, op))
        {
            if (r.Pid <= 0) continue;
            try
            {
                using var p = Process.GetProcessById(r.Pid);
                if (r.ProcessStartUtc is { } started && Math.Abs((p.StartTime.ToUniversalTime() - started.UtcDateTime).TotalSeconds) > 2) continue;   // a reused pid
                p.Kill(entireProcessTree: true);
                stopped.Add(r.Pid);
            }
            catch (ArgumentException) { }                       // already gone
            catch (InvalidOperationException) { }
        }
        return stopped;
    }
}
