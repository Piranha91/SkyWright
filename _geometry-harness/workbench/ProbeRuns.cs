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

    /// <summary>What the tile shows for a probe: waiting, running with its stage counters and ETA (or "no progress reported"
    /// and the elapsed time), done, failed or aborted.</summary>
    internal static ProbeProgress Of(string? store, string op, bool aborted, bool runEnded, DateTimeOffset now, RunEstimate? first)
    {
        if (store is null) return new("idle", null, "", null);
        var live = Active(store, op).FirstOrDefault();
        var done = Finished(store, op);
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
            var last = done.OrderBy(r => r.StartedUtc).Last();
            bool ok = done.All(r => r.Outcome == RunOutcome.Ok);
            return new(ok ? "done" : "failed", 1, (ok ? "done" : $"failed ({last.Error ?? last.Outcome.ToString()})") + $": {Runs(done.Count)}, {Clock(done.Sum(r => r.WallSeconds))}", null);
        }
        if (runEnded) return new("not run", null, "did not run this time", null);
        return new("waiting", null, "waiting its turn" + (first is { HasNumber: true } f ? $"; about {Clock(f.Seconds!.Value)} a run, judged from {Runs(f.Samples)} before" : ""), null);
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

    /// <summary>The first guess for one pass of a probe, by the run-log estimator over earlier runs' histories.</summary>
    internal static RunEstimate First(IEnumerable<string> historyFiles, string op, DateTimeOffset now)
    {
        var all = historyFiles.SelectMany(History).Where(r => string.Equals(r.Op, op, StringComparison.OrdinalIgnoreCase)).ToList();
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
